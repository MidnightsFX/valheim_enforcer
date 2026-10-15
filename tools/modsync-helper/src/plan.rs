//! The plan file ValheimEnforcer writes for this helper.
//!
//! One record per line, fields separated by a tab, the first field naming the record. A tab, newline, carriage
//! return or backslash inside a field is written as `\t`, `\n`, `\r` or `\\`. The first line is a version header,
//! and anything this build does not recognise is a refusal rather than a guess - a plan from a newer mod build is
//! not one to half-understand when the work is moving files around in a game install.
//!
//! ```text
//! ve-modsync	1
//! pid	12345
//! timeout	180
//! log	C:\...\modsync\apply.log
//! disable	C:\...\plugins\Some\Mod.dll
//! replace	C:\...\modsync\staged\Owner-Name	C:\...\plugins\Owner-Name	C:\...\modsync\replaced\20261014-120000
//! copy-new	C:\...\modsync\staged\Owner-Name.config\a.cfg	C:\...\config\a.cfg
//! cleanup	C:\...\modsync\staged
//! relaunch	explicit
//! exe	C:\...\valheim.exe
//! arg	--doorstop-enabled
//! arg	true
//! cwd	C:\...\Valheim
//! env	SteamAppId	892970
//! drop-arg-pair	+connect
//! append-arg	+connect
//! append-arg	1.2.3.4:2456
//! ```

use std::path::PathBuf;

pub const HEADER: &str = "ve-modsync";
pub const VERSION: &str = "1";

/// One file operation, carried out in the order the plan lists them.
#[derive(Debug, PartialEq)]
pub enum Op {
    /// Rename a plugin DLL to `<name>.old`, which is how r2modman and Gale mark a mod disabled.
    Disable(PathBuf),
    /// Move `target` into `backup` if it exists, then move `staged` to `target`.
    Replace { staged: PathBuf, target: PathBuf, backup: PathBuf },
    /// Copy `src` to `dst` only when nothing is at `dst` yet - a player's own config is never overwritten.
    CopyNew { src: PathBuf, dst: PathBuf },
}

/// How to start the game again once the changes are in.
#[derive(Debug, PartialEq)]
pub enum Relaunch {
    None,
    /// Exactly this program and these arguments (Windows, Proton and macOS). The environment is the plan's own
    /// `env` records when it has any, and this helper's environment otherwise.
    Explicit { exe: PathBuf, args: Vec<String>, cwd: Option<PathBuf> },
    /// Whatever the game process was started with, read from /proc while it is still running (Linux).
    Proc,
}

#[derive(Debug)]
pub struct Plan {
    pub pid: u32,
    pub timeout_secs: u64,
    pub log: PathBuf,
    pub ops: Vec<Op>,
    pub cleanup: Vec<PathBuf>,
    pub relaunch: Relaunch,
    /// Arguments dropped together with the value after them, e.g. a +connect the game was started with.
    pub drop_arg_pairs: Vec<String>,
    pub append_args: Vec<String>,
    /// The game's environment, when the mod wrote it into the plan rather than leaving it to be inherited. On
    /// macOS it has to: Doorstop is loaded there through DYLD_INSERT_LIBRARIES, which the mod keeps out of this
    /// helper's own environment so Doorstop is not injected into the helper too.
    pub env: Option<Vec<(String, String)>>,
}

pub fn parse(text: &str) -> Result<Plan, String> {
    let mut lines = text.lines().filter(|l| !l.is_empty());
    let header = lines.next().ok_or("the plan is empty")?;
    let header: Vec<String> = split(header)?;
    if header.len() != 2 || header[0] != HEADER || header[1] != VERSION {
        return Err(format!("not a plan this helper understands (header '{}')", header.join(" ")));
    }

    let mut pid = None;
    let mut timeout_secs = 180;
    let mut log = None;
    let mut ops = Vec::new();
    let mut cleanup = Vec::new();
    let mut mode = None;
    let mut exe = None;
    let mut args = Vec::new();
    let mut cwd = None;
    let mut drop_arg_pairs = Vec::new();
    let mut append_args = Vec::new();
    let mut env: Option<Vec<(String, String)>> = None;

    for line in lines {
        let fields = split(line)?;
        let want = |n: usize| -> Result<(), String> {
            if fields.len() == n + 1 { Ok(()) } else { Err(format!("'{}' takes {} value(s)", fields[0], n)) }
        };
        match fields[0].as_str() {
            "pid" => {
                want(1)?;
                pid = Some(fields[1].parse::<u32>().map_err(|_| format!("bad pid '{}'", fields[1]))?);
            }
            "timeout" => {
                want(1)?;
                timeout_secs = fields[1].parse::<u64>().map_err(|_| format!("bad timeout '{}'", fields[1]))?;
            }
            "log" => { want(1)?; log = Some(PathBuf::from(&fields[1])); }
            "disable" => { want(1)?; ops.push(Op::Disable(PathBuf::from(&fields[1]))); }
            "replace" => {
                want(3)?;
                ops.push(Op::Replace {
                    staged: PathBuf::from(&fields[1]),
                    target: PathBuf::from(&fields[2]),
                    backup: PathBuf::from(&fields[3]),
                });
            }
            "copy-new" => {
                want(2)?;
                ops.push(Op::CopyNew { src: PathBuf::from(&fields[1]), dst: PathBuf::from(&fields[2]) });
            }
            "cleanup" => { want(1)?; cleanup.push(PathBuf::from(&fields[1])); }
            "relaunch" => {
                want(1)?;
                mode = Some(match fields[1].as_str() {
                    "none" | "explicit" | "proc" => fields[1].clone(),
                    other => return Err(format!("unknown relaunch mode '{}'", other)),
                });
            }
            "exe" => { want(1)?; exe = Some(PathBuf::from(&fields[1])); }
            "arg" => { want(1)?; args.push(fields[1].clone()); }
            "cwd" => { want(1)?; cwd = Some(PathBuf::from(&fields[1])); }
            "drop-arg-pair" => { want(1)?; drop_arg_pairs.push(fields[1].clone()); }
            "append-arg" => { want(1)?; append_args.push(fields[1].clone()); }
            "env" => {
                want(2)?;
                if fields[1].is_empty() || fields[1].contains('=') {
                    return Err(format!("bad environment variable name '{}'", fields[1]));
                }
                env.get_or_insert_with(Vec::new).push((fields[1].clone(), fields[2].clone()));
            }
            other => return Err(format!("unknown record '{}'", other)),
        }
    }

    let relaunch = match mode.as_deref() {
        None | Some("none") => Relaunch::None,
        Some("proc") => Relaunch::Proc,
        _ => Relaunch::Explicit { exe: exe.ok_or("relaunch explicit needs an exe")?, args, cwd },
    };

    Ok(Plan {
        pid: pid.ok_or("the plan names no pid")?,
        timeout_secs,
        log: log.ok_or("the plan names no log")?,
        ops,
        cleanup,
        relaunch,
        drop_arg_pairs,
        append_args,
        env,
    })
}

fn split(line: &str) -> Result<Vec<String>, String> {
    line.trim_end_matches('\r').split('\t').map(unescape).collect()
}

fn unescape(field: &str) -> Result<String, String> {
    let mut out = String::with_capacity(field.len());
    let mut chars = field.chars();
    while let Some(c) = chars.next() {
        if c != '\\' {
            out.push(c);
            continue;
        }
        match chars.next() {
            Some('\\') => out.push('\\'),
            Some('t') => out.push('\t'),
            Some('n') => out.push('\n'),
            Some('r') => out.push('\r'),
            other => return Err(format!("bad escape '\\{}' in '{}'", other.map(String::from).unwrap_or_default(), field)),
        }
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn plan(body: &str) -> Result<Plan, String> {
        parse(&format!("ve-modsync\t1\npid\t42\nlog\t/tmp/x.log\n{}", body))
    }

    #[test]
    fn reads_a_full_plan() {
        // Written the way the mod writes it: '|' stands for the tab, and a backslash in a path is doubled.
        let body = r"timeout|5
disable|C:\\p\\A.dll
replace|s|t|b
copy-new|c1|c2
cleanup|st
relaunch|explicit
exe|C:\\Valheim\\valheim.exe
arg|-console
arg|with\ttab
cwd|C:\\Valheim
drop-arg-pair|+connect
append-arg|+connect
append-arg|1.2.3.4:2456
";
        let p = plan(&body.replace('|', "\t")).unwrap();
        assert_eq!(p.pid, 42);
        assert_eq!(p.timeout_secs, 5);
        assert_eq!(p.ops.len(), 3);
        assert_eq!(p.ops[0], Op::Disable(PathBuf::from("C:\\p\\A.dll")));
        assert_eq!(p.cleanup, vec![PathBuf::from("st")]);
        match p.relaunch {
            Relaunch::Explicit { exe, args, cwd } => {
                assert_eq!(exe, PathBuf::from("C:\\Valheim\\valheim.exe"));
                assert_eq!(args, vec!["-console".to_string(), "with\ttab".to_string()]);
                assert_eq!(cwd, Some(PathBuf::from("C:\\Valheim")));
            }
            other => panic!("{:?}", other),
        }
        assert_eq!(p.drop_arg_pairs, vec!["+connect"]);
        assert_eq!(p.append_args, vec!["+connect", "1.2.3.4:2456"]);
        assert!(p.env.is_none(), "no env records means inherit");
    }

    #[test]
    fn reads_environment_records() {
        let p = plan("relaunch\texplicit\nexe\t/V\nenv\tSteamAppId\t892970\nenv\tDOORSTOP_ENABLED\t1\nenv\tEMPTY\t\n").unwrap();
        assert_eq!(
            p.env.unwrap(),
            vec![("SteamAppId".into(), "892970".into()), ("DOORSTOP_ENABLED".into(), "1".into()), ("EMPTY".into(), "".into())]
        );
        assert!(plan("env\tA=B\tc\n").is_err());
        assert!(plan("env\t\tc\n").is_err());
        assert!(plan("env\tONLY_NAME\n").is_err());
    }

    #[test]
    fn escapes_round_trip() {
        assert_eq!(unescape("a\\\\b\\tc\\nd\\re").unwrap(), "a\\b\tc\nd\re");
        assert!(unescape("bad\\x").is_err());
        assert!(unescape("trailing\\").is_err());
    }

    #[test]
    fn refuses_what_it_does_not_understand() {
        assert!(parse("ve-modsync\t2\npid\t1\nlog\tx\n").is_err());
        assert!(parse("something else\n").is_err());
        assert!(plan("delete\t/\n").is_err());
        assert!(plan("disable\ta\tb\n").is_err());
        assert!(plan("relaunch\tsomehow\n").is_err());
        assert!(plan("relaunch\texplicit\n").is_err());
        assert!(parse("ve-modsync\t1\nlog\tx\n").is_err());
        assert!(parse("ve-modsync\t1\npid\t1\n").is_err());
    }

    #[test]
    fn windows_line_endings_are_fine() {
        let p = parse("ve-modsync\t1\r\npid\t7\r\nlog\tl\r\nrelaunch\tproc\r\n").unwrap();
        assert_eq!(p.pid, 7);
        assert_eq!(p.relaunch, Relaunch::Proc);
    }
}
