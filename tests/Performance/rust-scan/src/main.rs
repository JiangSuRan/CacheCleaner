use std::{env, fs, path::Path, time::Instant};
use std::os::windows::fs::MetadataExt;

#[derive(Debug, PartialEq)]
struct Scan { bytes: u64, files: u64, directories: u64, complete: bool }

fn measure(root: &Path) -> Scan {
    let mut result = Scan { bytes: 0, files: 0, directories: 0, complete: true };
    let mut pending = vec![root.to_owned()];
    while let Some(directory) = pending.pop() {
        match fs::symlink_metadata(&directory) {
            Ok(metadata) if metadata.file_attributes() & 0x400 != 0 => { result.complete = false; continue; }
            Ok(_) => (),
            Err(_) => { result.complete = false; continue; }
        }
        let entries = match fs::read_dir(&directory) {
            Ok(entries) => entries,
            Err(_) => { result.complete = false; continue; }
        };
        result.directories += 1;
        for entry in entries {
            let entry = match entry { Ok(entry) => entry, Err(_) => { result.complete = false; continue; } };
            let metadata = match entry.metadata() { Ok(metadata) => metadata, Err(_) => { result.complete = false; continue; } };
            if metadata.file_attributes() & 0x10 != 0 { pending.push(entry.path()); }
            else { result.bytes += metadata.len(); result.files += 1; }
        }
    }
    result
}

fn main() {
    let arguments: Vec<_> = env::args_os().collect();
    assert!(arguments.len() >= 2, "usage: cachecleaner-scan-prototype ROOT [ROUNDS]");
    let root = Path::new(&arguments[1]);
    assert!(root.is_dir(), "ROOT must exist");
    let rounds: usize = arguments.get(2).map(|value| value.to_string_lossy().parse().expect("invalid rounds")).unwrap_or(7);
    assert!(rounds > 0);
    let expected = measure(root);
    print!("{{\"Kind\":\"rust-read-only-prototype\",\"Measurements\":[");
    for index in 0..rounds {
        let start = Instant::now();
        let result = measure(root);
        let elapsed = start.elapsed().as_secs_f64() * 1000.0;
        assert_eq!(result, expected, "directory changed between rounds");
        if index > 0 { print!(","); }
        print!("{{\"WallMs\":{elapsed:.6},\"Bytes\":{},\"Files\":{},\"Directories\":{},\"Complete\":{}}}",result.bytes,result.files,result.directories,result.complete);
    }
    println!("]}}");
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn measures_nested_unicode_directory() {
        let root = env::temp_dir().join(format!("cachecleaner-rust-{}",std::process::id()));
        assert!(!root.exists());
        let child = root.join("目录 with spaces");
        fs::create_dir_all(&child).unwrap();
        fs::write(child.join("cache"), [0u8;37]).unwrap();
        let result = measure(&root);
        fs::remove_file(child.join("cache")).unwrap();
        fs::remove_dir(&child).unwrap();
        fs::remove_dir(&root).unwrap();
        assert_eq!(result, Scan { bytes:37, files:1, directories:2, complete:true });
    }
}
