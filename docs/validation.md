# 首版验证记录

验证日期：2026年10月4日

## 游戏身份

| 项目 | 实测值 |
| --- | --- |
| 平台 | Windows，Steam AppID 282070 |
| Steam BuildID | 22193501 |
| 游戏 EXE | x64/This War of Mine.exe |
| EXE SHA256 | 7E114E63D2371B3A31C6070011BA3869FECB2248895AFC0171B248C3E0B69BCB |
| Lua 运行时标识 | Lua5.1.1 |
| common 索引头 | 00 03 01 |
| common 条目数 | 1287 |
| Main 资源哈希 | 5faa28a2，对应 scripts/main.lua |
| 原始 common.idx SHA256 | 802D0D149749E7FEC352A00E2ECEE02B247C367F6713DA5C62EF1C575CD07D37 |
| 原始 common.dat SHA256 | F7939F6BF1F14F02E6887719C7D8C5E6DC34F4663ADEC709AF49A23BA8CF9C18 |

Lua 版本来自本机 EXE 中的标识，入口和压缩格式通过实际解压 Main 资源确认。未分发游戏源码或资源，表格仅记录版本指纹和兼容性信息。

## 验证项目

- Release 构建成功，零警告、零错误
- 21项 C# 自动化验证通过，包括中文 XML 注释、5000级依赖链、错误版本、索引越界、错误解压长度、安装恢复、第三方改动拒绝与中断恢复
- 独立 Lua5.1 验证通过，包括入口异常、无效入口协议、依赖失败跳过、事件取消、回调异常隔离、尾部 nil 参数、订阅快照、包装链和失败包装撤销
- 在全局 assert 被覆盖的测试环境中验证加载器仍执行自己的协议校验
- 将原始 common 容器备份后安装生成容器，通过 Steam 启动实际游戏
- 实际游戏日志记录欢迎模组入口成功及 loader.ready 回调
- 最终构建再次在实际游戏启动验证日记观察示例完成 logEvent 包装注册
- 测试结束恢复原容器，并核验两个 SHA256 指纹与安装前完全一致

## 实际游戏日志

```text
[TWOMLoader][twom.diary-observer] 已接入游戏logEvent函数
[TWOMLoader][twom.diary-observer] loaded
[TWOMLoader][twom.hello] 你好，《这是我的战争》！模组入口已经执行。
[TWOMLoader][twom.hello] loaded
[TWOMLoader][twom.hello] 所有可用模组加载完成。
```

以上日志来自游戏内 Lua 执行，不是命令行模拟。独立解释器测试使用游戏 API 占位函数，因此只验证脚本编译和加载器行为，不能替代实际游戏验证。

## 已知边界

- 当前只验证启动阶段，没有验证完整剧情、存档读写和长期游戏流程
- 日记观察示例仅包装 logEvent，没有提供全部原生事件
- 旧公开资料的 00 06 01 格式可解析，尚未在对应旧游戏版本运行验证
- 游戏可用模组内容限于现有 Lua 接口能力，自定义地图和模型仍需解析资源格式及原生引擎接口
- 当前通过容器部署加载，不具备原生 DLL 注入、C# 游戏内插件、热重载或不覆盖资源的虚拟挂载
- 官方模组和其他加载器修改同一容器时需要独立兼容性验证
