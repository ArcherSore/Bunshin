use crate::{MAX_MESSAGE_BYTES, error, files};
use serde::Deserialize;
use serde_json::{Value, json};
use std::fs;
use std::path::{Path, PathBuf};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub(crate) struct Request
{
    pub protocol_version: u32,
    pub operation: String,
    pub installation: String,
    pub reference: String,
    pub gui_pid: u32,
}

pub(crate) fn preflight(request: &str) -> Result<Request, String>
{
    if request.len() > MAX_MESSAGE_BYTES { return Err("安装命令过大。".into()); }
    let request: Request = serde_json::from_str(request).map_err(|e| e.to_string())?;
    if request.protocol_version != 1 || request.operation != "install" || request.gui_pid == 0 {
        return Err("安装命令无效。".into());
    }
    let root = Path::new(&request.installation);
    files::installation(root)?;
    let work = root.join("cache/updater");
    files::ancestors(&work)?;
    files::tree(&work)?;
    let payload = work.join("payload");
    if request.reference.is_empty()
        || fs::read_to_string(work.join("prepared")).map_err(|e| e.to_string())? != request.reference
        || !files::layout(&payload).is_some_and(|layout| payload.join(format!("{}.exe", layout.engine)).is_file())
        || work.join("old").exists() {
        return Err("已准备更新失效，请重新下载。".into());
    }
    // Verify writable workspace before handoff without modifying installed program files.
    let probe = work.join("write-probe");
    fs::write(&probe, []).and_then(|_| fs::remove_file(probe)).map_err(|e| e.to_string())?;
    let root_probe = root.join(format!(".updater-write-probe-{}", std::process::id()));
    let file = fs::OpenOptions::new().write(true).create_new(true).open(&root_probe).map_err(|e| e.to_string())?;
    drop(file);
    fs::remove_file(root_probe).map_err(|e| e.to_string())?;
    Ok(request)
}

/// Same install command boundary, with OS process operations substituted by filesystem tests.
pub fn install(
    request: &str, ready: impl FnOnce(Value) -> Result<(), String>,
    wait_gui: impl FnOnce(u32) -> Result<(), String>, relaunch: impl FnOnce(&Path) -> Result<(), String>,
    log: &mut dyn FnMut(&str),
) -> Value
{
    let request = match preflight(request) {
        Ok(request) => request,
        Err(message) => return error("install_preflight", &message),
    };
    run(&request, ready, wait_gui, relaunch, log, files::remove)
}

fn run(
    request: &Request, ready: impl FnOnce(Value) -> Result<(), String>,
    wait_gui: impl FnOnce(u32) -> Result<(), String>, relaunch: impl FnOnce(&Path) -> Result<(), String>,
    log: &mut dyn FnMut(&str), cleanup: impl Fn(&Path) -> Result<(), String>,
) -> Value
{
    if let Err(message) = ready(json!({"protocolVersion":1,"type":"ready","operation":"install"}))
        .and_then(|_| wait_gui(request.gui_pid)) {
        return error("install_preflight", &message);
    }
    let root = Path::new(&request.installation);
    // Joined per component so paths in failure messages use one separator.
    let work = root.join("cache").join("updater");
    let old = work.join("old");
    let payload = work.join("payload");
    let mut journal = Vec::new();
    if let Err(detail) = replace(root, &work, &mut journal) {
        log(&format!("安装失败，开始回滚：{detail}"));
        let unrestored = rollback(&journal, log);
        if !unrestored.is_empty() {
            return error("install_failed", &format!("安装失败：{detail}。回滚未完成，以下条目没有恢复：{}。\
                请从项目 GitHub 重新下载完整包；保留 config/logs/debug/cache 四个目录，清除其余程序内容后重新解压。\
                不要从 old 恢复。", unrestored.join("、")));
        }
        if old.exists() && let Err(e) = fs::remove_dir(&old) {
            log(&format!("回滚后删除 {} 失败：{e}", old.display()));
        }
        return error("install_failed", &format!("安装失败：{detail}。已恢复到更新前的版本，请重新启动程序。\
            如有资源管理器、终端等窗口停在安装目录内，请关闭后再更新。"));
    }
    for path in [&old, &payload] {
        if let Err(message) = cleanup(path) { log(&format!("安装后清理失败：{message}")); }
    }
    let launcher = files::layout(root).map_or(files::LAYOUTS[0].app, |layout| layout.app);
    if let Err(message) = relaunch(&root.join(format!("{launcher}.exe"))) {
        return error("relaunch_failed", &format!("文件安装完成但启动失败：{message}。请手动启动程序。"));
    }
    json!({"protocolVersion":1,"type":"result","operation":"install"})
}

/// Moves every managed root entry to `old`, then every Payload entry to the root. Each completed move is appended
/// to `journal`, so a failure part way can be walked back.
fn replace(root: &Path, work: &Path, journal: &mut Vec<(PathBuf, PathBuf)>) -> Result<(), String>
{
    let old = work.join("old");
    let payload = work.join("payload");
    files::installation(root)?;
    files::tree(&payload)?;
    fs::create_dir(&old).map_err(|e| format!("创建 {} 失败：{e}", old.display()))?;
    let prepared = work.join("prepared");
    fs::remove_file(&prepared).map_err(|e| format!("删除 {} 失败：{e}", prepared.display()))?;
    for (stage, from, to) in [("阶段 1 移走旧版", root, old.as_path()), ("阶段 2 放入新版", payload.as_path(), root)] {
        // Every old managed entry has left the root before the first new entry is installed.
        let entries = fs::read_dir(from).map_err(|e| format!("{stage}：读取 {} 失败：{e}", from.display()))?;
        for entry in entries {
            let name = entry.map_err(|e| format!("{stage}：读取 {} 失败：{e}", from.display()))?.file_name();
            if from == root && files::preserved(&name.to_string_lossy()) { continue; }
            let (source, target) = (from.join(&name), to.join(&name));
            retry(|| fs::rename(&source, &target)).map_err(|e| {
                format!("{stage}：移动 {} 到 {} 失败：{e}", source.display(), target.display())
            })?;
            journal.push((source, target));
        }
    }
    Ok(())
}

/// Moves journaled entries back in reverse order and returns the original paths that could not be restored.
fn rollback(journal: &[(PathBuf, PathBuf)], log: &mut dyn FnMut(&str)) -> Vec<String>
{
    let mut unrestored = Vec::new();
    for (original, moved) in journal.iter().rev() {
        let result = if original.symlink_metadata().is_ok() {
            Err("原位置已被占用".to_owned())
        } else {
            retry(|| fs::rename(moved, original)).map_err(|e| e.to_string())
        };
        if let Err(e) = result {
            log(&format!("回滚：将 {} 移回 {} 失败：{e}", moved.display(), original.display()));
            unrestored.push(original.display().to_string());
        }
    }
    log(&format!("回滚结束：{} 项已恢复，{} 项未恢复。", journal.len() - unrestored.len(), unrestored.len()));
    unrestored
}

/// Retries a file operation with backoff, for short locks such as antivirus scans of a just-moved file.
fn retry<T>(mut operation: impl FnMut() -> std::io::Result<T>) -> std::io::Result<T>
{
    let mut delay = 200;
    for _ in 1..RETRY_ATTEMPTS {
        match operation() {
            Ok(value) => return Ok(value),
            Err(_) => std::thread::sleep(std::time::Duration::from_millis(delay)),
        }
        delay *= 2;
    }
    operation()
}

const RETRY_ATTEMPTS: u32 = 5;

#[cfg(test)]
#[test]
fn rollback_reports_entries_whose_original_place_is_taken()
{
    let root = std::env::temp_dir().join(format!("rollback-occupied-{}", std::process::id()));
    let _ = fs::remove_dir_all(&root);
    fs::create_dir_all(root.join("old")).unwrap();
    fs::write(root.join("old/a"), "old a").unwrap();
    fs::write(root.join("old/b"), "old b").unwrap();
    fs::write(root.join("b"), "new b").unwrap();
    let journal = vec![(root.join("a"), root.join("old/a")), (root.join("b"), root.join("old/b"))];
    let mut logged = Vec::new();
    let unrestored = rollback(&journal, &mut |message| logged.push(message.to_owned()));
    assert_eq!(unrestored, vec![root.join("b").display().to_string()]);
    assert_eq!(fs::read_to_string(root.join("a")).unwrap(), "old a");
    assert_eq!(fs::read_to_string(root.join("b")).unwrap(), "new b");
    assert!(logged.iter().any(|line| line.contains("原位置已被占用")), "{logged:?}");
    fs::remove_dir_all(root).unwrap();
}

#[cfg(test)]
#[test]
fn cleanup_failure_does_not_prevent_relaunch()
{
    let root = std::env::temp_dir().join(format!("cleanup-failure-{}", std::process::id()));
    fs::create_dir_all(root.join("cache/updater/payload")).unwrap();
    fs::write(root.join("cache/updater/prepared"), "opaque").unwrap();
    fs::write(root.join("cache/updater/payload/Bunshin.exe"), "new").unwrap();
    fs::write(root.join("cache/updater/payload/update-engine.exe"), "new").unwrap();
    let request = json!({"protocolVersion":1,"operation":"install","installation":root,
        "reference":"opaque","guiPid":123}).to_string();
    let mut logged = Vec::new();
    let result = run(&preflight(&request).unwrap(), |_| Ok(()), |_| Ok(()), |exe| {
        assert!(exe.exists());
        Ok(())
    }, &mut |message| logged.push(message.to_owned()), |_| Err("injected cleanup failure".into()));
    assert_eq!(result["type"], "result");
    assert_eq!(logged.len(), 2);
    fs::remove_dir_all(root).unwrap();
}

#[cfg(windows)]
pub fn handoff(request: &str, copy_parent: Option<u32>) -> Value
{
    use crate::native;
    use std::io::Write;
    if let Some(pid) = copy_parent {
        let parent = match native::Process::open(pid) {
            Ok(parent) => parent,
            Err(message) => return error("install_preflight", &message),
        };
        if let Err(message) = parent.wait(10000) { return error("install_preflight", &message); }
    }
    let parsed = match preflight(request) {
        Ok(parsed) => parsed,
        Err(message) => return error("install_preflight", &message),
    };
    let root = Path::new(&parsed.installation);
    if copy_parent.is_none() {
        return match native::copy_and_launch(request, root) {
            Ok(()) => std::process::exit(0),
            Err(message) => error("install_preflight", &message),
        };
    }
    let expected = root.join("cache/updater/run/update-engine.exe");
    if std::env::current_exe().ok().as_deref() != Some(expected.as_path()) {
        return error("install_preflight", "安装副本位置无效。");
    }
    let gui = match native::Process::open(parsed.gui_pid) {
        Ok(gui) => gui,
        Err(message) => return error("install_preflight", &message),
    };
    let logs = root.join("logs");
    let _ = fs::create_dir_all(&logs);
    let log_path = logs.join("updater.log");
    let mut log = |message: &str| {
        if let Ok(mut file) = fs::OpenOptions::new().create(true).append(true).open(&log_path) {
            let _ = writeln!(file, "install: {message}");
        }
    };
    let mut sent_ready = false;
    let response = run(&parsed, |value| {
        if !gui.running() { return Err("GUI 已在接管前退出。".into()); }
        let mut output = std::io::stdout().lock();
        writeln!(output, "{value}").and_then(|_| output.flush()).map_err(|e| e.to_string())?;
        sent_ready = true;
        Ok(())
    }, |_| gui.wait(30000), native::relaunch, &mut log, files::remove);
    if sent_ready && response["type"] == "error" {
        native::failure(&format!("{}\n日志：{}", response["message"].as_str().unwrap_or("安装失败"), log_path.display()));
    }
    response
}
