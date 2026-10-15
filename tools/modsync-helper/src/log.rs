//! What the helper did, one line per step. ValheimEnforcer reads this file on the next start and copies it into
//! the BepInEx log, so a player's log says what happened while the game was closed.

use std::fs::{File, OpenOptions};
use std::io::Write;
use std::path::Path;

pub struct Log {
    file: Option<File>,
    /// What was written, for the tests to read back.
    #[cfg_attr(not(test), allow(dead_code))]
    kept: Vec<String>,
}

impl Log {
    pub fn create(path: &Path) -> Log {
        let file = OpenOptions::new().create(true).write(true).truncate(true).open(path).ok();
        Log { file, kept: Vec::new() }
    }

    #[cfg(test)]
    pub fn memory() -> Log {
        Log { file: None, kept: Vec::new() }
    }

    pub fn line(&mut self, text: &str) {
        // Flushed line by line: if the helper is killed partway, the lines up to that point are what tells the
        // player which changes landed.
        if let Some(file) = self.file.as_mut() {
            let _ = writeln!(file, "{}", text);
            let _ = file.flush();
        }
        self.kept.push(text.to_string());
    }

    #[cfg(test)]
    pub fn lines(&self) -> Vec<&str> {
        self.kept.iter().map(String::as_str).collect()
    }
}
