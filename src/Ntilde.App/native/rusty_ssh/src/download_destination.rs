//! Directory-download filesystem authority. Server-supplied names are resolved
//! below an opened root, never by reopening absolute paths after validation.
use cap_fs_ext::DirExt;
use cap_std::{ambient_authority, fs::Dir};
use std::{
    ffi::OsStr,
    io,
    path::{Component, Path},
};

pub(super) fn basename(path: &Path) -> io::Result<&OsStr> {
    path.file_name().ok_or_else(|| {
        io::Error::new(
            io::ErrorKind::InvalidInput,
            "Download path needs a filename",
        )
    })
}

pub(super) fn open_root(path: &Path) -> io::Result<Dir> {
    // The caller selected this parent. The appended remote basename is already
    // validated, but an existing link at that basename is not authority to leave it.
    let parent = path
        .parent()
        .filter(|p| !p.as_os_str().is_empty())
        .unwrap_or(Path::new("."));
    std::fs::create_dir_all(parent)?;
    let parent = Dir::open_ambient_dir(parent, ambient_authority())?;
    create_child(&parent, basename(path)?)
}

pub(super) fn open_directory(root: &Dir, relative: &Path) -> io::Result<Dir> {
    let mut directory = root.try_clone()?;
    for component in relative.components() {
        let Component::Normal(name) = component else {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Download directory must be relative",
            ));
        };
        directory = create_child(&directory, name)?;
    }
    Ok(directory)
}

fn create_child(parent: &Dir, name: &OsStr) -> io::Result<Dir> {
    match parent.create_dir(name) {
        Ok(()) => {}
        Err(error) if error.kind() == io::ErrorKind::AlreadyExists => {}
        Err(error) => return Err(error),
    }
    parent.open_dir_nofollow(name)
}
