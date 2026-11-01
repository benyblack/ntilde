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
    #[cfg(windows)]
    let created = std::fs::create_dir(windows::child_path(parent, name)?);
    #[cfg(not(windows))]
    let created = parent.create_dir(name);
    match created {
        Ok(()) => {}
        Err(error) if error.kind() == io::ErrorKind::AlreadyExists => {}
        Err(error) => return Err(error),
    }
    parent.open_dir_nofollow(name)
}

pub(super) fn rename_file(directory: &Dir, source: &OsStr, destination: &OsStr) -> io::Result<()> {
    #[cfg(windows)]
    return std::fs::rename(
        windows::child_path(directory, source)?,
        windows::child_path(directory, destination)?,
    );
    #[cfg(not(windows))]
    directory.rename(source, directory, destination)
}

pub(super) fn remove_file(directory: &Dir, name: &OsStr) -> io::Result<()> {
    #[cfg(windows)]
    return std::fs::remove_file(windows::child_path(directory, name)?);
    #[cfg(not(windows))]
    directory.remove_file(name)
}

#[cfg(windows)]
mod windows {
    use super::*;
    use std::{
        ffi::OsString,
        os::windows::{ffi::OsStringExt, io::AsRawHandle},
        path::PathBuf,
    };
    use windows_sys::Win32::Storage::FileSystem::GetFinalPathNameByHandleW;

    // cap's directory handles deny FILE_SHARE_DELETE, pinning their paths while
    // held. Preserve the extended path the OS returns: stripping \\?\ turns a
    // UNC path into relative UNC\server\share and breaks cap-primitives 4.0.3's
    // Windows create/rename/remove helpers. Use those helpers only on Unix.
    pub(super) fn child_path(directory: &Dir, name: &OsStr) -> io::Result<PathBuf> {
        let mut components = Path::new(name).components();
        if !matches!(
            (components.next(), components.next()),
            (Some(Component::Normal(_)), None)
        ) {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Download name must be one component",
            ));
        }
        let handle = directory.as_raw_handle();
        let mut buffer = vec![0u16; 512];
        loop {
            // The handle is owned by directory and stays live throughout both
            // length-query/retry and the subsequent caller's filesystem operation.
            let length = unsafe {
                GetFinalPathNameByHandleW(handle, buffer.as_mut_ptr(), buffer.len() as u32, 0)
            };
            if length == 0 {
                return Err(io::Error::last_os_error());
            }
            if length as usize >= buffer.len() {
                buffer.resize(length as usize + 1, 0);
                continue;
            }
            let path = PathBuf::from(OsString::from_wide(&buffer[..length as usize]));
            if !path.is_absolute() {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "Download handle has no absolute path",
                ));
            }
            return Ok(path.join(name));
        }
    }
}
