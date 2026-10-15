//! Carrying out the plan's file operations.
//!
//! Each operation stands alone: one that fails is logged and the rest still run, because a half-applied plan
//! is better described than abandoned partway with no record. Nothing is deleted except the staging folder;
//! a replaced mod folder is moved aside and a turned-off DLL is renamed.

use crate::log::Log;
use crate::plan::Op;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};

pub fn run(ops: &[Op], log: &mut Log) {
    for op in ops {
        match op {
            Op::Disable(dll) => disable(dll, log),
            Op::Replace { staged, target, backup } => replace(staged, target, backup, log),
            Op::CopyNew { src, dst } => copy_new(src, dst, log),
        }
    }
}

fn name(path: &Path) -> String {
    path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| path.display().to_string())
}

fn disable(dll: &Path, log: &mut Log) {
    if !dll.exists() {
        return;
    }
    let mut old = dll.as_os_str().to_owned();
    old.push(".old");
    let old = PathBuf::from(old);
    // Windows will not rename over an existing file. An older .old of the same mod is the only thing it can be.
    if old.exists() {
        let _ = fs::remove_file(&old);
    }
    match fs::rename(dll, &old) {
        Ok(()) => log.line(&format!("Turned off {}", name(dll))),
        Err(e) => log.line(&format!("FAILED to turn off {}: {}", name(dll), e)),
    }
}

fn replace(staged: &Path, target: &Path, backup: &Path, log: &mut Log) {
    let label = name(target);
    if target.exists() {
        let aside = free_name(&backup.join(&label));
        let moved = fs::create_dir_all(backup).and_then(|_| move_dir(target, &aside));
        match moved {
            Ok(()) => log.line(&format!("Moved the old {} aside", label)),
            Err(e) => log.line(&format!("FAILED to move the old {} aside: {}", label, e)),
        }
    }
    if target.exists() {
        log.line(&format!("FAILED: {} is still there, so the new one was not installed", label));
        return;
    }
    let installed = target.parent().map_or(Ok(()), fs::create_dir_all).and_then(|_| move_dir(staged, target));
    match installed {
        Ok(()) => log.line(&format!("Installed {}", label)),
        Err(e) => log.line(&format!("FAILED to install {}: {}", label, e)),
    }
}

fn copy_new(src: &Path, dst: &Path, log: &mut Log) {
    if dst.exists() {
        return;
    }
    let copied = dst.parent().map_or(Ok(()), fs::create_dir_all).and_then(|_| fs::copy(src, dst).map(|_| ()));
    match copied {
        Ok(()) => log.line(&format!("Added config {}", name(dst))),
        Err(e) => log.line(&format!("FAILED to add config {}: {}", name(dst), e)),
    }
}

/// `path`, or `path-2`, `path-3`... whichever is not taken. Two runs in the same second would otherwise collide.
fn free_name(path: &Path) -> PathBuf {
    if !path.exists() {
        return path.to_path_buf();
    }
    for n in 2.. {
        let mut candidate = path.as_os_str().to_owned();
        candidate.push(format!("-{}", n));
        let candidate = PathBuf::from(candidate);
        if !candidate.exists() {
            return candidate;
        }
    }
    unreachable!()
}

/// A rename, or a copy and delete when the rename crosses filesystems. Staging lives beside the plugins folder
/// so the rename is the normal case; the copy is for a plugins folder that is a symlink to another drive.
fn move_dir(from: &Path, to: &Path) -> io::Result<()> {
    match fs::rename(from, to) {
        Ok(()) => Ok(()),
        Err(rename_error) => {
            if !from.is_dir() {
                return Err(rename_error);
            }
            copy_dir(from, to)?;
            fs::remove_dir_all(from)
        }
    }
}

fn copy_dir(from: &Path, to: &Path) -> io::Result<()> {
    fs::create_dir_all(to)?;
    for entry in fs::read_dir(from)? {
        let entry = entry?;
        let dest = to.join(entry.file_name());
        if entry.file_type()?.is_dir() {
            copy_dir(&entry.path(), &dest)?;
        } else {
            fs::copy(entry.path(), dest)?;
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sandbox(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("ve-modsync-test-{}-{}", name, std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        dir
    }

    fn write(path: &Path, text: &str) {
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(path, text).unwrap();
    }

    fn read(path: &Path) -> String {
        fs::read_to_string(path).unwrap()
    }

    #[test]
    fn applies_every_kind_of_operation() {
        let root = sandbox("apply");
        let plugins = root.join("plugins");
        let staged = root.join("staged");
        let config = root.join("config");
        let backup = root.join("replaced").join("stamp");
        write(&plugins.join("Extra").join("Extra 50%.dll"), "extra");
        write(&plugins.join("Extra").join("Extra 50%.dll.old"), "older");
        write(&plugins.join("Own-Enf").join("Enf.dll"), "old");
        write(&staged.join("Own-Enf").join("Enf.dll"), "new");
        write(&staged.join("Own-New").join("New.dll"), "fresh");
        write(&staged.join("cfg").join("a.cfg"), "from package");
        write(&staged.join("cfg").join("b.cfg"), "from package");
        write(&config.join("b.cfg"), "player's own");
        // A backup from an earlier run in the same second.
        write(&backup.join("Own-Enf").join("Enf.dll"), "earlier");

        let ops = vec![
            Op::Disable(plugins.join("Extra").join("Extra 50%.dll")),
            Op::Disable(plugins.join("Missing.dll")),
            Op::Replace { staged: staged.join("Own-Enf"), target: plugins.join("Own-Enf"), backup: backup.clone() },
            Op::Replace { staged: staged.join("Own-New"), target: plugins.join("Own-New"), backup: backup.clone() },
            Op::CopyNew { src: staged.join("cfg").join("a.cfg"), dst: config.join("sub").join("a.cfg") },
            Op::CopyNew { src: staged.join("cfg").join("b.cfg"), dst: config.join("b.cfg") },
        ];
        let mut log = Log::memory();
        run(&ops, &mut log);

        assert!(!plugins.join("Extra").join("Extra 50%.dll").exists());
        assert_eq!(read(&plugins.join("Extra").join("Extra 50%.dll.old")), "extra");
        assert_eq!(read(&plugins.join("Own-Enf").join("Enf.dll")), "new");
        assert_eq!(read(&backup.join("Own-Enf-2").join("Enf.dll")), "old");
        assert_eq!(read(&backup.join("Own-Enf").join("Enf.dll")), "earlier");
        assert_eq!(read(&plugins.join("Own-New").join("New.dll")), "fresh");
        assert_eq!(read(&config.join("sub").join("a.cfg")), "from package");
        assert_eq!(read(&config.join("b.cfg")), "player's own");
        assert_eq!(
            log.lines(),
            vec![
                "Turned off Extra 50%.dll",
                "Moved the old Own-Enf aside",
                "Installed Own-Enf",
                "Installed Own-New",
                "Added config a.cfg",
            ]
        );
        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn a_missing_staged_folder_is_reported_not_fatal() {
        let root = sandbox("missing");
        let mut log = Log::memory();
        run(
            &[
                Op::Replace { staged: root.join("nope"), target: root.join("plugins").join("X"), backup: root.join("b") },
                Op::CopyNew { src: root.join("nope.cfg"), dst: root.join("config").join("x.cfg") },
            ],
            &mut log,
        );
        let lines = log.lines();
        assert_eq!(lines.len(), 2);
        assert!(lines[0].starts_with("FAILED to install X"));
        assert!(lines[1].starts_with("FAILED to add config x.cfg"));
        let _ = fs::remove_dir_all(&root);
    }
}
