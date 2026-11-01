use super::*;
use russh_sftp::protocol::{
    Attrs, Data, File, FileAttributes, Handle, Name, OpenFlags, Status, StatusCode,
};
use std::collections::HashSet;

#[derive(Default)]
struct ReviewServer {
    read_dirs: HashSet<String>,
    nested: bool,
    fail_read: bool,
    on_read: Option<Box<dyn FnOnce() + Send>>,
    on_open: Option<Box<dyn FnOnce() + Send>>,
}
fn directory() -> FileAttributes {
    FileAttributes::default()
}
fn regular() -> FileAttributes {
    FileAttributes {
        permissions: Some(0o100644),
        size: Some(6),
        ..FileAttributes::empty()
    }
}
impl russh_sftp::server::Handler for ReviewServer {
    type Error = StatusCode;
    fn unimplemented(&self) -> Self::Error {
        StatusCode::OpUnsupported
    }
    async fn opendir(&mut self, id: u32, path: String) -> Result<Handle, Self::Error> {
        Ok(Handle { id, handle: path })
    }
    async fn readdir(&mut self, id: u32, handle: String) -> Result<Name, Self::Error> {
        if !self.read_dirs.insert(handle.clone()) {
            return Err(StatusCode::Eof);
        }
        let files = match handle.as_str() {
            "/remote" => vec![File::new("link", directory())],
            "/remote/link" if self.nested => vec![File::new("deep", directory())],
            "/remote/link" | "/remote/link/deep" => vec![File::new("victim.txt", regular())],
            _ => vec![],
        };
        Ok(Name { id, files })
    }
    async fn stat(&mut self, id: u32, path: String) -> Result<Attrs, Self::Error> {
        Ok(Attrs {
            id,
            attrs: if path.ends_with("victim.txt") {
                regular()
            } else {
                directory()
            },
        })
    }
    async fn open(
        &mut self,
        id: u32,
        filename: String,
        _: OpenFlags,
        _: FileAttributes,
    ) -> Result<Handle, Self::Error> {
        if let Some(callback) = self.on_open.take() {
            callback();
        }
        Ok(Handle {
            id,
            handle: filename,
        })
    }
    async fn read(&mut self, id: u32, _: String, offset: u64, _: u32) -> Result<Data, Self::Error> {
        if let Some(callback) = self.on_read.take() {
            callback();
        }
        if self.fail_read {
            return Err(StatusCode::Failure);
        }
        if offset >= 6 {
            Err(StatusCode::Eof)
        } else {
            Ok(Data {
                id,
                data: b"remote"[offset as usize..].to_vec(),
            })
        }
    }
    async fn close(&mut self, id: u32, _: String) -> Result<Status, Self::Error> {
        Ok(Status {
            id,
            status_code: StatusCode::Ok,
            error_message: String::new(),
            language_tag: String::new(),
        })
    }
    async fn realpath(&mut self, id: u32, _: String) -> Result<Name, Self::Error> {
        Ok(Name {
            id,
            files: vec![File::dummy("/home/ops")],
        })
    }
}
async fn session(server_handler: ReviewServer) -> SftpSession {
    let (client, server) = tokio::io::duplex(65536);
    russh_sftp::server::run(server, server_handler).await;
    SftpSession::new(client).await.unwrap()
}

struct Tree(PathBuf);
impl Tree {
    fn new() -> Self {
        let path = std::env::temp_dir().join(format!(
            "ntilde-download-{}-{}",
            std::process::id(),
            PARTIAL_DOWNLOAD_COUNTER.fetch_add(1, Ordering::Relaxed)
        ));
        std::fs::create_dir_all(&path).unwrap();
        Self(path)
    }
    fn path(&self, name: &str) -> PathBuf {
        self.0.join(name)
    }
}
impl Drop for Tree {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn directory_link(target: &Path, link: &Path) {
    #[cfg(unix)]
    std::os::unix::fs::symlink(target, link).unwrap();
    #[cfg(windows)]
    {
        // Junctions need no developer-mode or symbolic-link privilege.
        let result = std::process::Command::new("cmd.exe")
            .args(["/d", "/c", "mklink", "/J"])
            .arg(link)
            .arg(target)
            .output()
            .unwrap();
        assert!(
            result.status.success(),
            "junction creation failed: {:?}",
            result
        );
    }
}

async fn download(
    local_root: &Path,
    server: ReviewServer,
    cancel: Option<&str>,
) -> anyhow::Result<()> {
    let sftp = session(server).await;
    let result = download_directory_from_remote(
        &sftp,
        "/remote",
        local_root,
        cancel,
        SftpProgressEmitter {
            callback: None,
            context: std::ptr::null_mut(),
        },
        &mut [0; 1024],
    )
    .await;
    let _ = sftp.close().await;
    result
}

#[tokio::test]
async fn directory_download_refuses_existing_child_link() {
    let tree = Tree::new();
    let local = tree.path("download");
    let outside = tree.path("outside");
    std::fs::create_dir_all(&local).unwrap();
    std::fs::create_dir_all(&outside).unwrap();
    std::fs::write(outside.join("victim.txt"), b"keep me").unwrap();
    directory_link(&outside, &local.join("link"));
    assert!(
        download(&local, ReviewServer::default(), None)
            .await
            .is_err()
    );
    assert_eq!(
        std::fs::read(outside.join("victim.txt")).unwrap(),
        b"keep me"
    );
}

#[tokio::test]
async fn directory_download_refuses_nested_ancestor_link() {
    let tree = Tree::new();
    let local = tree.path("download");
    let outside = tree.path("outside");
    std::fs::create_dir_all(local.join("link")).unwrap();
    std::fs::create_dir_all(&outside).unwrap();
    std::fs::write(outside.join("victim.txt"), b"keep me").unwrap();
    directory_link(&outside, &local.join("link/deep"));
    assert!(
        download(
            &local,
            ReviewServer {
                nested: true,
                ..Default::default()
            },
            None
        )
        .await
        .is_err()
    );
    assert_eq!(
        std::fs::read(outside.join("victim.txt")).unwrap(),
        b"keep me"
    );
}

#[tokio::test]
async fn directory_download_refuses_link_at_remote_basename() {
    let tree = Tree::new();
    let outside = tree.path("outside");
    std::fs::create_dir_all(&outside).unwrap();
    directory_link(&outside, &tree.path("download"));
    assert!(
        download(&tree.path("download"), ReviewServer::default(), None)
            .await
            .is_err()
    );
    assert_eq!(std::fs::read_dir(outside).unwrap().count(), 0);
}

#[tokio::test]
async fn directory_download_creates_nested_directories_and_replaces_file_atomically() {
    let tree = Tree::new();
    let local = tree.path("download");
    std::fs::create_dir_all(local.join("link/deep")).unwrap();
    std::fs::write(local.join("link/deep/victim.txt"), b"old").unwrap();
    download(
        &local,
        ReviewServer {
            nested: true,
            ..Default::default()
        },
        None,
    )
    .await
    .unwrap();
    assert_eq!(
        std::fs::read(local.join("link/deep/victim.txt")).unwrap(),
        b"remote"
    );
    assert_eq!(
        std::fs::read_dir(local.join("link/deep")).unwrap().count(),
        1
    );
}

#[tokio::test]
async fn failed_directory_file_keeps_original_and_removes_scratch() {
    let tree = Tree::new();
    let local = tree.path("download");
    std::fs::create_dir_all(local.join("link")).unwrap();
    std::fs::write(local.join("link/victim.txt"), b"keep me").unwrap();
    assert!(
        download(
            &local,
            ReviewServer {
                fail_read: true,
                ..Default::default()
            },
            None
        )
        .await
        .is_err()
    );
    assert_eq!(
        std::fs::read(local.join("link/victim.txt")).unwrap(),
        b"keep me"
    );
    assert_eq!(std::fs::read_dir(local.join("link")).unwrap().count(), 1);
}

#[tokio::test]
async fn cancelled_directory_file_keeps_original_and_removes_scratch() {
    let tree = Tree::new();
    let local = tree.path("download");
    let marker = tree.path("cancel");
    let callback_marker = marker.clone();
    std::fs::create_dir_all(local.join("link")).unwrap();
    std::fs::write(local.join("link/victim.txt"), b"keep me").unwrap();
    let server = ReviewServer {
        on_open: Some(Box::new(move || {
            std::fs::write(callback_marker, b"cancel").unwrap()
        })),
        ..Default::default()
    };
    assert!(download(&local, server, marker.to_str()).await.is_err());
    assert_eq!(
        std::fs::read(local.join("link/victim.txt")).unwrap(),
        b"keep me"
    );
    assert_eq!(std::fs::read_dir(local.join("link")).unwrap().count(), 1);
}

#[tokio::test]
#[cfg(unix)]
async fn ancestor_swap_after_scratch_creation_cannot_redirect_commit() {
    let tree = Tree::new();
    let local = tree.path("download");
    let outside = tree.path("outside");
    std::fs::create_dir_all(local.join("link")).unwrap();
    std::fs::create_dir_all(&outside).unwrap();
    std::fs::write(outside.join("victim.txt"), b"keep me").unwrap();
    let original = local.join("link");
    let moved = local.join("moved");
    let outside_link = outside.clone();
    let server = ReviewServer {
        on_read: Some(Box::new(move || {
            std::fs::rename(&original, &moved).unwrap();
            directory_link(&outside_link, &original);
        })),
        ..Default::default()
    };
    download(&local, server, None).await.unwrap();
    assert_eq!(
        std::fs::read(outside.join("victim.txt")).unwrap(),
        b"keep me"
    );
    assert_eq!(
        std::fs::read(local.join("moved/victim.txt")).unwrap(),
        b"remote"
    );
    assert_eq!(std::fs::read_dir(local.join("moved")).unwrap().count(), 1);
}

#[test]
fn remote_basename_rejects_a_backslash_component_before_local_join() {
    assert!(remote_basename(r"/remote/root\child").is_err());
}
