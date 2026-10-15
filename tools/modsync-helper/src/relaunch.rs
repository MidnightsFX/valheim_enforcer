//! Starting the game again, the way it was started the first time.
//!
//! BepInEx is loaded by Doorstop, and how Doorstop is set up differs by platform. On Windows (and under Proton)
//! it is a proxy DLL beside the game configured by doorstop_config.ini or command line arguments, so repeating the
//! game's command line is enough. On Linux it is LD_PRELOAD plus DOORSTOP_* environment variables that a launch
//! script or mod manager set, so the environment has to come back exactly too - which is why the Linux relaunch
//! reads the game's own command line, environment, executable and working directory out of /proc while it is
//! still running, rather than trusting what the game process thinks its environment is by the time it quits.
//! macOS has no /proc and loads Doorstop through DYLD_INSERT_LIBRARIES, so there the mod hands over the command
//! line and writes the environment into the plan; it keeps DYLD_INSERT_LIBRARIES out of this helper's own
//! environment, or Doorstop would be loaded into the helper as well.
//!
//! Either way, Doorstop's own runtime markers are removed. Doorstop sets these inside the game once it has run;
//! a new game process that inherits DOORSTOP_INITIALIZED skips loading BepInEx altogether. Its configuration
//! variables are left alone, because on Linux they are the configuration.

use crate::plan::{Plan, Relaunch};
use std::ffi::{OsStr, OsString};
use std::path::PathBuf;
use std::process::{Command, Stdio};

/// The variables Doorstop sets in a process it has loaded into (UnityDoorstop src/bootstrap.c and the
/// platform entrypoints), as opposed to the DOORSTOP_* settings it reads.
pub const DOORSTOP_MARKERS: &[&str] = &[
    "DOORSTOP_INITIALIZED",
    "DOORSTOP_DISABLE",
    "DOORSTOP_INVOKE_DLL_PATH",
    "DOORSTOP_PROCESS_PATH",
    "DOORSTOP_MANAGED_FOLDER_DIR",
    "DOORSTOP_DLL_SEARCH_DIRS",
    "DOORSTOP_MONO_LIB_PATH",
];

#[derive(Debug)]
pub struct Launch {
    pub exe: PathBuf,
    pub args: Vec<OsString>,
    pub cwd: Option<PathBuf>,
    pub env: Vec<(OsString, OsString)>,
}

/// Works out the launch before the game exits - on Linux the information is gone once it has.
pub fn prepare(plan: &Plan) -> Result<Option<Launch>, String> {
    let launch = match &plan.relaunch {
        Relaunch::None => return Ok(None),
        Relaunch::Explicit { exe, args, cwd } => Launch {
            exe: exe.clone(),
            args: args.iter().map(OsString::from).collect(),
            cwd: cwd.clone(),
            env: match &plan.env {
                Some(env) => env.iter().map(|(k, v)| (OsString::from(k), OsString::from(v))).collect(),
                None => std::env::vars_os().collect(),
            },
        },
        Relaunch::Proc => from_proc(plan.pid)?,
    };
    Ok(Some(Launch {
        args: edit_args(launch.args, &plan.drop_arg_pairs, &plan.append_args),
        env: without_markers(launch.env),
        ..launch
    }))
}

/// Drops each named argument together with the value after it, then appends the new ones - a +connect the game
/// was started with is replaced by the join back to the server that asked for these mods.
pub fn edit_args(args: Vec<OsString>, drop_pairs: &[String], append: &[String]) -> Vec<OsString> {
    let mut out = Vec::with_capacity(args.len() + append.len());
    let mut iter = args.into_iter().peekable();
    while let Some(arg) = iter.next() {
        if drop_pairs.iter().any(|d| OsStr::new(d) == arg) && iter.peek().is_some() {
            iter.next();
            continue;
        }
        out.push(arg);
    }
    out.extend(append.iter().map(OsString::from));
    out
}

pub fn without_markers(env: Vec<(OsString, OsString)>) -> Vec<(OsString, OsString)> {
    env.into_iter()
        .filter(|(key, _)| {
            let key = key.to_string_lossy();
            !DOORSTOP_MARKERS.iter().any(|m| key.eq_ignore_ascii_case(m))
        })
        .collect()
}

pub fn spawn(launch: &Launch) -> std::io::Result<()> {
    let mut command = Command::new(&launch.exe);
    command.args(&launch.args).env_clear().envs(launch.env.iter().map(|(k, v)| (k, v))).stdin(Stdio::null());
    if let Some(cwd) = &launch.cwd {
        command.current_dir(cwd);
    }
    // Not waited for: the helper's job ends when the game is running again.
    command.spawn().map(|_| ())
}

#[cfg(target_os = "linux")]
fn from_proc(pid: u32) -> Result<Launch, String> {
    use std::os::unix::ffi::OsStringExt;
    let read = |what: &str| std::fs::read(format!("/proc/{}/{}", pid, what)).map_err(|e| format!("reading /proc/{}/{}: {}", pid, what, e));
    let link = |what: &str| std::fs::read_link(format!("/proc/{}/{}", pid, what)).map_err(|e| format!("reading /proc/{}/{}: {}", pid, what, e));

    let exe = link("exe")?;
    let cwd = link("cwd").ok();
    // NUL-separated. argv[0] is how the game was invoked, not something to pass back as an argument.
    let args = split_nul(&read("cmdline")?).into_iter().skip(1).map(OsString::from_vec).collect();
    let env = split_nul(&read("environ")?)
        .into_iter()
        .filter_map(|pair| {
            let at = pair.iter().position(|&b| b == b'=')?;
            Some((OsString::from_vec(pair[..at].to_vec()), OsString::from_vec(pair[at + 1..].to_vec())))
        })
        .collect();
    Ok(Launch { exe, args, cwd, env })
}

#[cfg(not(target_os = "linux"))]
fn from_proc(_pid: u32) -> Result<Launch, String> {
    Err("this platform has no /proc to read the game's launch from".to_string())
}

#[cfg_attr(not(target_os = "linux"), allow(dead_code))]
fn split_nul(bytes: &[u8]) -> Vec<Vec<u8>> {
    bytes.split(|&b| b == 0).filter(|part| !part.is_empty()).map(|part| part.to_vec()).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn os(v: &[&str]) -> Vec<OsString> {
        v.iter().map(OsString::from).collect()
    }

    #[test]
    fn replaces_the_join_and_keeps_everything_else() {
        let args = os(&["--doorstop-target-assembly", "C:\\Some Path (x86)\\BepInEx.Preloader.dll", "+connect", "old:1", "-console", "+connect_lobby", "123", "trailing\\"]);
        let out = edit_args(args, &["+connect".into(), "+connect_lobby".into()], &["+connect".into(), "1.2.3.4:2456".into()]);
        assert_eq!(out, os(&["--doorstop-target-assembly", "C:\\Some Path (x86)\\BepInEx.Preloader.dll", "-console", "trailing\\", "+connect", "1.2.3.4:2456"]));
    }

    #[test]
    fn a_dangling_join_flag_is_kept() {
        assert_eq!(edit_args(os(&["-console", "+connect"]), &["+connect".into()], &[]), os(&["-console", "+connect"]));
    }

    #[test]
    fn strips_doorstop_markers_but_not_its_settings() {
        let env = vec![
            ("DOORSTOP_INITIALIZED".into(), "TRUE".into()),
            ("doorstop_disable".into(), "TRUE".into()),
            ("DOORSTOP_INVOKE_DLL_PATH".into(), "x".into()),
            ("DOORSTOP_ENABLED".into(), "1".into()),
            ("DOORSTOP_TARGET_ASSEMBLY".into(), "/game/BepInEx/core/BepInEx.Preloader.dll".into()),
            ("LD_PRELOAD".into(), "libdoorstop_x64.so".into()),
            ("SteamAppId".into(), "892970".into()),
        ];
        let kept: Vec<String> = without_markers(env).into_iter().map(|(k, _)| k.to_string_lossy().into_owned()).collect();
        assert_eq!(kept, vec!["DOORSTOP_ENABLED", "DOORSTOP_TARGET_ASSEMBLY", "LD_PRELOAD", "SteamAppId"]);
    }

    #[test]
    fn an_explicit_launch_takes_the_plan_environment_when_there_is_one() {
        let base = "ve-modsync\t1\npid\t1\nlog\tl\nrelaunch\texplicit\nexe\t/game\n";
        let with = crate::plan::parse(&format!("{}env\tSteamAppId\t892970\nenv\tDOORSTOP_INITIALIZED\tTRUE\n", base)).unwrap();
        let launch = prepare(&with).unwrap().unwrap();
        assert_eq!(launch.env, vec![(OsString::from("SteamAppId"), OsString::from("892970"))]);

        let without = crate::plan::parse(base).unwrap();
        let launch = prepare(&without).unwrap().unwrap();
        assert_eq!(launch.env.len(), without_markers(std::env::vars_os().collect()).len(), "no env records: inherited");
    }

    #[test]
    fn splits_proc_lists() {
        assert_eq!(split_nul(b"a\0b c\0\0"), vec![b"a".to_vec(), b"b c".to_vec()]);
    }
}
