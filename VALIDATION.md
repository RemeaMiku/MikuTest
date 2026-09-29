# 验证

本文件只记录可复现的验证方式，不保存本机账号、真实试题、上传文件路径或操作日志。

```sh
dotnet build MikuTest.slnx
dotnet run --project tests/MikuTest.SmokeTests --no-build
```

集成检查覆盖题库与题集复用、初始化与升级、公共材料、筛选与随机组卷、排序、评分及历史快照、上传校验、Identity、权限与审计。

测试在系统临时目录创建独立数据库。测试代码中的账号和密码均为虚构夹具，不是站点登录凭据。

2026-09-29 整理版本：.NET 10 构建 0 警告、0 错误；122 项检查通过。C#、项目文件、Razor 逻辑块及自有 CSS/JavaScript/JSON 已格式化，JavaScript 语法检查通过。
