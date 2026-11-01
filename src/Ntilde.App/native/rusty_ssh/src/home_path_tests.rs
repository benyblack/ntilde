use super::{
    PARTIAL_DOWNLOAD_COUNTER, SftpProgressEmitter, SftpTransferRequestBody, transfer_on_sftp,
};
use russh_sftp::client::SftpSession;
use russh_sftp::protocol::{
    Attrs, Data, File, FileAttributes, Handle, Name, OpenFlags, Status, StatusCode,
};
use std::collections::{BTreeMap, HashSet};
use std::path::{Path, PathBuf};
use std::sync::atomic::Ordering;
use std::sync::{Arc, Mutex};

#[derive(Default)]
struct RemoteTree {
    directories: HashSet<String>,
    files: BTreeMap<String, Vec<u8>>,
    paths: Vec<String>,
    home_resolutions: usize,
}
struct Server {
    tree: Arc<Mutex<RemoteTree>>,
    read_dirs: HashSet<String>,
}
fn attrs(directory: bool, size: usize) -> FileAttributes {
    FileAttributes {
        permissions: Some(if directory { 0o040755 } else { 0o100644 }),
        size: Some(size as u64),
        ..FileAttributes::empty()
    }
}
fn ok(id: u32) -> Status {
    Status {
        id,
        status_code: StatusCode::Ok,
        error_message: String::new(),
        language_tag: String::new(),
    }
}
impl russh_sftp::server::Handler for Server {
    type Error = StatusCode;
    fn unimplemented(&self) -> Self::Error {
        StatusCode::OpUnsupported
    }
    async fn realpath(&mut self, id: u32, path: String) -> Result<Name, Self::Error> {
        assert_eq!(path, ".");
        self.tree.lock().unwrap().home_resolutions += 1;
        Ok(Name {
            id,
            files: vec![File::dummy("/home/ops")],
        })
    }
    async fn stat(&mut self, id: u32, path: String) -> Result<Attrs, Self::Error> {
        let mut tree = self.tree.lock().unwrap();
        tree.paths.push(path.clone());
        if tree.directories.contains(&path) {
            return Ok(Attrs {
                id,
                attrs: attrs(true, 0),
            });
        }
        tree.files
            .get(&path)
            .map(|bytes| Attrs {
                id,
                attrs: attrs(false, bytes.len()),
            })
            .ok_or(StatusCode::NoSuchFile)
    }
    async fn lstat(&mut self, id: u32, path: String) -> Result<Attrs, Self::Error> {
        self.stat(id, path).await
    }
    async fn fstat(&mut self, id: u32, handle: String) -> Result<Attrs, Self::Error> {
        self.stat(id, handle).await
    }
    async fn open(
        &mut self,
        id: u32,
        path: String,
        flags: OpenFlags,
        _: FileAttributes,
    ) -> Result<Handle, Self::Error> {
        let mut tree = self.tree.lock().unwrap();
        tree.paths.push(path.clone());
        if flags.contains(OpenFlags::CREATE) {
            let parent = path
                .rsplit_once('/')
                .map(|(parent, _)| parent)
                .unwrap_or(".");
            if !tree.directories.contains(parent) {
                return Err(StatusCode::NoSuchFile);
            }
            tree.files.insert(path.clone(), Vec::new());
        }
        if !tree.files.contains_key(&path) {
            return Err(StatusCode::NoSuchFile);
        }
        Ok(Handle { id, handle: path })
    }
    async fn read(
        &mut self,
        id: u32,
        handle: String,
        offset: u64,
        len: u32,
    ) -> Result<Data, Self::Error> {
        let tree = self.tree.lock().unwrap();
        let bytes = tree.files.get(&handle).ok_or(StatusCode::NoSuchFile)?;
        let offset = offset as usize;
        if offset >= bytes.len() {
            return Err(StatusCode::Eof);
        }
        Ok(Data {
            id,
            data: bytes[offset..bytes.len().min(offset + len as usize)].to_vec(),
        })
    }
    async fn write(
        &mut self,
        id: u32,
        handle: String,
        offset: u64,
        data: Vec<u8>,
    ) -> Result<Status, Self::Error> {
        let mut tree = self.tree.lock().unwrap();
        let bytes = tree.files.get_mut(&handle).ok_or(StatusCode::NoSuchFile)?;
        let offset = offset as usize;
        bytes.resize(bytes.len().max(offset + data.len()), 0);
        bytes[offset..offset + data.len()].copy_from_slice(&data);
        Ok(ok(id))
    }
    async fn close(&mut self, id: u32, _: String) -> Result<Status, Self::Error> {
        Ok(ok(id))
    }
    async fn mkdir(
        &mut self,
        id: u32,
        path: String,
        _: FileAttributes,
    ) -> Result<Status, Self::Error> {
        let mut tree = self.tree.lock().unwrap();
        tree.paths.push(path.clone());
        tree.directories.insert(path);
        Ok(ok(id))
    }
    async fn opendir(&mut self, id: u32, path: String) -> Result<Handle, Self::Error> {
        let mut tree = self.tree.lock().unwrap();
        tree.paths.push(path.clone());
        if !tree.directories.contains(&path) {
            return Err(StatusCode::NoSuchFile);
        }
        Ok(Handle { id, handle: path })
    }
    async fn readdir(&mut self, id: u32, handle: String) -> Result<Name, Self::Error> {
        if !self.read_dirs.insert(handle.clone()) {
            return Err(StatusCode::Eof);
        }
        let tree = self.tree.lock().unwrap();
        let prefix = format!("{handle}/");
        let files = tree
            .files
            .iter()
            .map(|(p, b)| (p, attrs(false, b.len())))
            .chain(tree.directories.iter().map(|p| (p, attrs(true, 0))))
            .filter_map(|(path, attrs)| {
                path.strip_prefix(&prefix)
                    .filter(|name| !name.contains('/'))
                    .map(|name| File::new(name, attrs))
            })
            .collect();
        Ok(Name { id, files })
    }
}
struct Fixture {
    local: PathBuf,
    remote: Arc<Mutex<RemoteTree>>,
    sftp: SftpSession,
}
impl Fixture {
    async fn new() -> Self {
        let local = std::env::temp_dir().join(format!(
            "ntilde-home-{}-{}",
            std::process::id(),
            PARTIAL_DOWNLOAD_COUNTER.fetch_add(1, Ordering::Relaxed)
        ));
        tokio::fs::create_dir_all(local.join("upload"))
            .await
            .unwrap();
        tokio::fs::write(local.join("upload.txt"), b"upload bytes")
            .await
            .unwrap();
        tokio::fs::write(local.join("upload/part.txt"), b"upload bytes")
            .await
            .unwrap();
        let remote = Arc::new(Mutex::new(RemoteTree::default()));
        {
            let mut tree = remote.lock().unwrap();
            tree.directories
                .extend(["/", "/home", "/home/ops", "/home/ops/inbox"].map(str::to_owned));
            tree.files.extend(
                [
                    "/home/ops/victim.txt",
                    "/home/ops/inbox/victim.txt",
                    "/home/ops/inbox/trailing ",
                    r"relative\file ",
                ]
                .map(|p| (p.to_owned(), b"download bytes".to_vec())),
            );
        }
        let (client, server) = tokio::io::duplex(65536);
        russh_sftp::server::run(
            server,
            Server {
                tree: remote.clone(),
                read_dirs: HashSet::new(),
            },
        )
        .await;
        let sftp = SftpSession::new(client).await.unwrap();
        Self {
            local,
            remote,
            sftp,
        }
    }
    async fn transfer(
        &self,
        direction: &str,
        kind: &str,
        remote_path: &str,
        local_path: &Path,
    ) -> anyhow::Result<()> {
        transfer_on_sftp(
            &self.sftp,
            &SftpTransferRequestBody {
                direction: direction.to_owned(),
                kind: kind.to_owned(),
                local_path: local_path.to_str().unwrap().to_owned(),
                remote_path: remote_path.to_owned(),
                cancellation_marker_path: None,
            },
            SftpProgressEmitter {
                callback: None,
                context: std::ptr::null_mut(),
            },
        )
        .await
    }
    fn assert_no_literal_home_path(&self) {
        let tree = self.remote.lock().unwrap();
        assert!(!tree.paths.is_empty());
        assert_eq!(tree.home_resolutions, 1);
        assert!(
            tree.paths.iter().all(|p| !p.starts_with('~')),
            "{:?}",
            tree.paths
        );
        assert!(!tree.directories.contains("~"));
    }
}
impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.local);
    }
}

#[tokio::test]
async fn file_upload_expands_bare_home_and_home_subdirectory() {
    for (remote, destination) in [
        ("~", "/home/ops/upload.txt"),
        ("~/inbox", "/home/ops/inbox/upload.txt"),
    ] {
        let fixture = Fixture::new().await;
        fixture
            .transfer("upload", "file", remote, &fixture.local.join("upload.txt"))
            .await
            .unwrap();
        assert_eq!(
            fixture.remote.lock().unwrap().files[destination],
            b"upload bytes"
        );
        fixture.assert_no_literal_home_path();
    }
}
#[tokio::test]
async fn directory_upload_expands_bare_home_and_home_subdirectory() {
    for (remote, destination) in [
        ("~", "/home/ops/upload/part.txt"),
        ("~/inbox", "/home/ops/inbox/upload/part.txt"),
    ] {
        let fixture = Fixture::new().await;
        fixture
            .transfer("upload", "directory", remote, &fixture.local.join("upload"))
            .await
            .unwrap();
        assert_eq!(
            fixture.remote.lock().unwrap().files[destination],
            b"upload bytes"
        );
        fixture.assert_no_literal_home_path();
    }
}
#[tokio::test]
async fn file_download_expands_home_subdirectory() {
    let fixture = Fixture::new().await;
    let local = fixture.local.join("download.txt");
    fixture
        .transfer("download", "file", "~/inbox/victim.txt", &local)
        .await
        .unwrap();
    assert_eq!(std::fs::read(local).unwrap(), b"download bytes");
    fixture.assert_no_literal_home_path();
}
#[tokio::test]
async fn directory_download_expands_home_before_choosing_local_basename() {
    for (remote, basename) in [("~", "ops"), ("~/inbox", "inbox")] {
        let fixture = Fixture::new().await;
        let local = fixture.local.join("download");
        fixture
            .transfer("download", "directory", remote, &local)
            .await
            .unwrap();
        assert_eq!(
            std::fs::read(local.join(basename).join("victim.txt")).unwrap(),
            b"download bytes"
        );
        assert!(!local.join("~").exists());
        fixture.assert_no_literal_home_path();
    }
}
#[tokio::test]
async fn file_download_preserves_literal_backslashes_and_spaces() {
    for remote in [
        r"relative\file ",
        "/home/ops/inbox/trailing ",
        "~/inbox/trailing ",
    ] {
        let fixture = Fixture::new().await;
        let local = fixture.local.join("download.txt");
        fixture
            .transfer("download", "file", remote, &local)
            .await
            .unwrap();
        assert_eq!(std::fs::read(local).unwrap(), b"download bytes");
    }
}

#[tokio::test]
async fn file_upload_preserves_a_literal_padded_tilde_filename() {
    let fixture = Fixture::new().await;
    {
        let mut tree = fixture.remote.lock().unwrap();
        tree.directories.insert(".".to_owned());
        tree.files.insert("~ ".to_owned(), b"old bytes".to_vec());
    }
    fixture
        .transfer("upload", "file", "~ ", &fixture.local.join("upload.txt"))
        .await
        .unwrap();
    let tree = fixture.remote.lock().unwrap();
    assert_eq!(tree.files["~ "], b"upload bytes");
    assert_eq!(tree.home_resolutions, 0);
}
