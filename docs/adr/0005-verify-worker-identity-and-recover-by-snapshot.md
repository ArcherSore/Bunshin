# 用系统身份接纳 Worker，并用完整 Snapshot 恢复观察

同一 Child Session 只接纳一个 Worker。GUI 依据 Pipe 对端的真实进程与 Windows Session 身份，再结合启动凭据核验 Worker；不能把可重连的 Pipe 连接或客户端自报 PID 当作身份。重连后以完整 Snapshot 恢复权威运行观察，而非重放可能丢失的事件；这增加接纳和同步逻辑，但避免错误接管另一个进程或凭不完整事件推测 Run 结果。

## 2026-11-22 更新：Worker 退出时读到 EOF 再结算

确认 Worker 进程退出后，GUI 让原连接的读循环继续提交已读帧，读到 EOF 后才发布进程退出证据。若 5 秒内仍未读到 EOF（例如其他进程继承了管道句柄），GUI 强制关闭该连接，按已收到的证据结算 Run。

保留一条双向管道和现有协议版本。Windows 命名管道原型验证了子进程写完即退出时，服务端仍能读完缓冲帧；立即断开则会丢失未读数据。该决定沿用了 .NET、Node.js 和 Go 对进程退出与输出读完的区分。[父票中的原型与调研依据](https://github.com/ArcherSore/Bunshin/issues/154)
