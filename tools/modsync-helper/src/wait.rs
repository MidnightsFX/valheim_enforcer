//! Waiting for the game to exit.
//!
//! The watch is set up the moment the helper starts, while the game is still running - the game starts this
//! helper and only then quits - so a process id reused after the game has gone cannot be mistaken for it:
//! Windows holds a handle to the process itself, Linux remembers the process's start time, and macOS registers
//! a kqueue exit notification on the process.

use std::time::Duration;

pub enum Outcome {
    Exited,
    TimedOut,
}

#[cfg(windows)]
mod imp {
    use super::*;

    const SYNCHRONIZE: u32 = 0x0010_0000;
    const WAIT_OBJECT_0: u32 = 0;
    const WAIT_TIMEOUT: u32 = 0x102;
    const ERROR_INVALID_PARAMETER: u32 = 87;

    #[link(name = "kernel32")]
    extern "system" {
        fn OpenProcess(access: u32, inherit: i32, pid: u32) -> isize;
        fn WaitForSingleObject(handle: isize, millis: u32) -> u32;
        fn CloseHandle(handle: isize) -> i32;
        fn GetLastError() -> u32;
    }

    pub struct Watch(Option<isize>);

    impl Watch {
        pub fn open(pid: u32) -> Result<Watch, String> {
            let handle = unsafe { OpenProcess(SYNCHRONIZE, 0, pid) };
            if handle != 0 {
                return Ok(Watch(Some(handle)));
            }
            match unsafe { GetLastError() } {
                // No such process: it has already gone.
                ERROR_INVALID_PARAMETER => Ok(Watch(None)),
                code => Err(format!("could not watch Valheim's process (Windows error {})", code)),
            }
        }

        pub fn wait(&self, timeout: Duration) -> Result<Outcome, String> {
            let handle = match self.0 {
                Some(h) => h,
                None => return Ok(Outcome::Exited),
            };
            let millis = timeout.as_millis().min(u32::MAX as u128 - 1) as u32;
            match unsafe { WaitForSingleObject(handle, millis) } {
                WAIT_OBJECT_0 => Ok(Outcome::Exited),
                WAIT_TIMEOUT => Ok(Outcome::TimedOut),
                other => Err(format!("waiting for Valheim failed (code {})", other)),
            }
        }
    }

    impl Drop for Watch {
        fn drop(&mut self) {
            if let Some(h) = self.0 {
                unsafe { CloseHandle(h) };
            }
        }
    }
}

#[cfg(target_os = "linux")]
mod imp {
    use super::*;
    use std::time::Instant;

    pub struct Watch {
        pid: u32,
        /// Field 22 of /proc/<pid>/stat: when the process started. A new process given the same id has a
        /// different one.
        start: Option<u64>,
    }

    impl Watch {
        pub fn open(pid: u32) -> Result<Watch, String> {
            Ok(Watch { pid, start: read_stat(pid).filter(|s| !s.gone).map(|s| s.start) })
        }

        pub fn wait(&self, timeout: Duration) -> Result<Outcome, String> {
            let start = match self.start {
                Some(s) => s,
                None => return Ok(Outcome::Exited),
            };
            let deadline = Instant::now() + timeout;
            loop {
                match read_stat(self.pid) {
                    None => return Ok(Outcome::Exited),
                    Some(s) if s.gone || s.start != start => return Ok(Outcome::Exited),
                    _ => {}
                }
                if Instant::now() >= deadline {
                    return Ok(Outcome::TimedOut);
                }
                std::thread::sleep(Duration::from_millis(200));
            }
        }
    }

    pub struct Stat {
        pub start: u64,
        /// Zombie or dead: it has exited and is only waiting to be reaped.
        pub gone: bool,
    }

    fn read_stat(pid: u32) -> Option<Stat> {
        std::fs::read_to_string(format!("/proc/{}/stat", pid)).ok().and_then(|s| parse_stat(&s))
    }

    /// The process name sits in parentheses and may itself contain spaces or parentheses, so fields are counted
    /// from the last ')'. After it: state is field 3, start time field 22.
    pub fn parse_stat(stat: &str) -> Option<Stat> {
        let rest = &stat[stat.rfind(')')? + 1..];
        let fields: Vec<&str> = rest.split_whitespace().collect();
        let state = fields.first()?;
        let start = fields.get(19)?.parse::<u64>().ok()?;
        Some(Stat { start, gone: *state == "Z" || *state == "X" })
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        #[test]
        fn reads_start_time_and_state() {
            let s = parse_stat("1234 (valheim (x)) S 1 1234 1234 0 -1 4194560 1 2 3 4 5 6 7 8 20 0 30 0 987654 1 2").unwrap();
            assert_eq!(s.start, 987654);
            assert!(!s.gone);
            assert!(parse_stat("9 (z) Z 1 1 1 0 -1 0 0 0 0 0 0 0 0 0 20 0 1 0 55 0 0").unwrap().gone);
            assert!(parse_stat("garbage").is_none());
        }
    }
}

/// macOS: a kqueue exit notification. Registering it binds it to the process itself, the way a Windows handle
/// does, so it fires when the game exits and cannot be confused by a later process given the same id.
#[cfg(target_os = "macos")]
mod imp {
    use super::*;
    use std::time::Instant;

    // <sys/event.h>. On 64-bit macOS struct kevent is 32 bytes: ident, filter, flags, fflags, data, udata.
    #[repr(C)]
    struct KEvent {
        ident: usize,
        filter: i16,
        flags: u16,
        fflags: u32,
        data: isize,
        udata: *mut std::ffi::c_void,
    }

    #[repr(C)]
    struct Timespec {
        tv_sec: i64,
        tv_nsec: i64,
    }

    const EVFILT_PROC: i16 = -5;
    const EV_ADD: u16 = 0x0001;
    const EV_ONESHOT: u16 = 0x0010;
    const EV_ERROR: u16 = 0x4000;
    const NOTE_EXIT: u32 = 0x8000_0000;
    const ESRCH: i32 = 3;
    const EINTR: i32 = 4;

    extern "C" {
        fn kqueue() -> i32;
        fn kevent(kq: i32, changes: *const KEvent, nchanges: i32, events: *mut KEvent, nevents: i32, timeout: *const Timespec) -> i32;
        fn close(fd: i32) -> i32;
    }

    fn errno() -> Option<i32> {
        std::io::Error::last_os_error().raw_os_error()
    }

    /// `None` when the process had already gone by the time the watch was set up.
    pub struct Watch(Option<i32>);

    impl Watch {
        pub fn open(pid: u32) -> Result<Watch, String> {
            let kq = unsafe { kqueue() };
            if kq < 0 {
                return Err(format!("could not watch Valheim's process (kqueue errno {:?})", errno()));
            }
            let change = KEvent {
                ident: pid as usize,
                filter: EVFILT_PROC,
                flags: EV_ADD | EV_ONESHOT,
                fflags: NOTE_EXIT,
                data: 0,
                udata: std::ptr::null_mut(),
            };
            // With no room for receipts, a failed registration comes back as -1 and errno.
            if unsafe { kevent(kq, &change, 1, std::ptr::null_mut(), 0, std::ptr::null()) } < 0 {
                let code = errno();
                unsafe { close(kq) };
                return match code {
                    Some(ESRCH) => Ok(Watch(None)),
                    other => Err(format!("could not watch Valheim's process (kevent errno {:?})", other)),
                };
            }
            Ok(Watch(Some(kq)))
        }

        pub fn wait(&self, timeout: Duration) -> Result<Outcome, String> {
            let kq = match self.0 {
                Some(kq) => kq,
                None => return Ok(Outcome::Exited),
            };
            let deadline = Instant::now() + timeout;
            loop {
                let left = deadline.saturating_duration_since(Instant::now());
                let ts = Timespec { tv_sec: left.as_secs() as i64, tv_nsec: left.subsec_nanos() as i64 };
                let mut event = KEvent { ident: 0, filter: 0, flags: 0, fflags: 0, data: 0, udata: std::ptr::null_mut() };
                match unsafe { kevent(kq, std::ptr::null(), 0, &mut event, 1, &ts) } {
                    0 => return Ok(Outcome::TimedOut),
                    n if n > 0 && event.flags & EV_ERROR != 0 => {
                        return Err(format!("waiting for Valheim failed (kevent error {})", event.data));
                    }
                    n if n > 0 => return Ok(Outcome::Exited),
                    _ if errno() == Some(EINTR) => continue,
                    _ => return Err(format!("waiting for Valheim failed (kevent errno {:?})", errno())),
                }
            }
        }
    }

    impl Drop for Watch {
        fn drop(&mut self) {
            if let Some(kq) = self.0 {
                unsafe { close(kq) };
            }
        }
    }
}

/// Any other Unix. Not a shipped build; this only keeps the crate compiling there.
#[cfg(all(unix, not(any(target_os = "linux", target_os = "macos"))))]
mod imp {
    use super::*;
    use std::time::Instant;

    extern "C" {
        fn kill(pid: i32, sig: i32) -> i32;
    }

    pub struct Watch(u32);

    impl Watch {
        pub fn open(pid: u32) -> Result<Watch, String> {
            Ok(Watch(pid))
        }

        pub fn wait(&self, timeout: Duration) -> Result<Outcome, String> {
            let deadline = Instant::now() + timeout;
            loop {
                // Signal 0 only asks whether the process exists, so a reused id would read as the game.
                if unsafe { kill(self.0 as i32, 0) } != 0 && std::io::Error::last_os_error().raw_os_error() == Some(3) {
                    return Ok(Outcome::Exited);
                }
                if Instant::now() >= deadline {
                    return Ok(Outcome::TimedOut);
                }
                std::thread::sleep(Duration::from_millis(200));
            }
        }
    }
}

/// Against real processes, so every platform's watch is exercised on that platform: Windows and Linux here, macOS
/// on the GitHub runner.
#[cfg(test)]
mod tests {
    use super::*;
    use std::process::{Child, Command};
    use std::time::Instant;

    fn sleeper(seconds: u32) -> Child {
        use std::process::Stdio;
        // No standard handles at all, so this cannot be upset by the relaunch test pointing them elsewhere.
        #[cfg(windows)]
        let child = Command::new("ping").args(["-n", &(seconds + 1).to_string(), "127.0.0.1"]).stdin(Stdio::null()).stdout(Stdio::null()).stderr(Stdio::null()).spawn();
        #[cfg(unix)]
        let child = Command::new("sleep").arg(seconds.to_string()).stdin(Stdio::null()).stdout(Stdio::null()).stderr(Stdio::null()).spawn();
        child.expect("start a short-lived process")
    }

    #[test]
    fn sees_a_process_exit() {
        let mut child = sleeper(1);
        let watch = Watch::open(child.id()).unwrap();
        let started = Instant::now();
        assert!(matches!(watch.wait(Duration::from_secs(20)).unwrap(), Outcome::Exited));
        assert!(started.elapsed() < Duration::from_secs(10));
        let _ = child.wait();
    }

    #[test]
    fn times_out_on_a_process_that_keeps_running() {
        let mut child = sleeper(10);
        let watch = Watch::open(child.id()).unwrap();
        assert!(matches!(watch.wait(Duration::from_millis(300)).unwrap(), Outcome::TimedOut));
        let _ = child.kill();
        let _ = child.wait();
    }

    #[test]
    fn a_process_already_gone_counts_as_exited() {
        let mut child = sleeper(0);
        let pid = child.id();
        let _ = child.wait();
        drop(child);
        let watch = Watch::open(pid).unwrap();
        assert!(matches!(watch.wait(Duration::from_secs(5)).unwrap(), Outcome::Exited));
    }
}

pub use imp::Watch;
