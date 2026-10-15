//! ve-modsync: applies the mod changes ValheimEnforcer's mod sync staged, once Valheim has exited, then starts
//! the game again.
//!
//! The changes cannot be made from inside the game, because every loaded plugin DLL is locked for as long as the
//! process lives. So the game downloads and checks everything, writes a plan, starts this helper with the plan's
//! path, and quits. This helper waits for the game process to be gone, carries out the plan, and relaunches.
//!
//! It is a native binary rather than a script so one program behaves the same everywhere the mod runs: Windows,
//! Proton (the Windows build, under Wine), native Linux (a static build, which also keeps Doorstop's LD_PRELOAD
//! from loading into it) and macOS (an x86_64 build: Doorstop only exists for x86_64 there, so a Mac running
//! BepInEx runs Valheim as x86_64, under Rosetta on Apple Silicon, and Rosetta is therefore already installed). It does nothing the plan does not name, takes no network access, and has no
//! dependencies outside the Rust standard library.
//!
//!     ve-modsync <plan file>
//!
//! Exit codes: 0 applied (or nothing to apply), 1 the game never exited and nothing was changed, 2 bad usage or
//! an unreadable plan, 3 the game's process could not be watched and nothing was changed.

// No console window on Windows: the helper's output is its log file.
#![cfg_attr(windows, windows_subsystem = "windows")]

mod apply;
mod log;
mod plan;
mod relaunch;
mod wait;

use log::Log;
use std::path::PathBuf;
use std::process::exit;
use std::time::Duration;

#[cfg(unix)]
extern "C" {
    fn setsid() -> i32;
}

fn main() {
    let args: Vec<_> = std::env::args_os().collect();
    if args.len() != 2 {
        exit(2);
    }
    let plan_path = PathBuf::from(&args[1]);
    let plan = match std::fs::read_to_string(&plan_path).map_err(|e| e.to_string()).and_then(|t| plan::parse(&t)) {
        Ok(p) => p,
        Err(e) => {
            // No log path is known without a plan, so the reason goes beside the plan itself.
            let _ = std::fs::write(plan_path.with_extension("error.log"), format!("FAILED to read the plan: {}\n", e));
            exit(2);
        }
    };
    // The plan holds nothing worth keeping once read, and a stale one must never be run twice.
    let _ = std::fs::remove_file(&plan_path);

    let mut log = Log::create(&plan.log);
    log.line("ValheimEnforcer mod sync helper started");

    // Its own session, so closing the terminal the game was started from does not take the helper with it.
    #[cfg(unix)]
    unsafe {
        setsid();
    }

    // Both taken now, while the game is still running: the watch so a reused process id cannot fool it, and the
    // launch because on Linux it is read out of the running game's /proc entry.
    let watch = match wait::Watch::open(plan.pid) {
        Ok(w) => w,
        Err(e) => {
            log.line(&format!("FAILED: {}. Nothing was changed.", e));
            cleanup(&plan.cleanup);
            exit(3);
        }
    };
    let launch = match relaunch::prepare(&plan) {
        Ok(l) => l,
        Err(e) => {
            log.line(&format!("FAILED to work out how to start Valheim again ({}); start it yourself once this finishes", e));
            None
        }
    };

    match watch.wait(Duration::from_secs(plan.timeout_secs)) {
        Ok(wait::Outcome::Exited) => {}
        Ok(wait::Outcome::TimedOut) => {
            log.line(&format!("Valheim did not exit within {} seconds. Nothing was changed.", plan.timeout_secs));
            cleanup(&plan.cleanup);
            exit(1);
        }
        Err(e) => {
            log.line(&format!("FAILED: {}. Nothing was changed.", e));
            cleanup(&plan.cleanup);
            exit(3);
        }
    }

    apply::run(&plan.ops, &mut log);
    cleanup(&plan.cleanup);

    if let Some(launch) = launch {
        match relaunch::spawn(&launch) {
            Ok(()) => log.line("Started Valheim again"),
            Err(e) => log.line(&format!("FAILED to start Valheim again: {}", e)),
        }
    }
}

fn cleanup(dirs: &[PathBuf]) {
    for dir in dirs {
        let _ = std::fs::remove_dir_all(dir);
    }
}
