# MikuTest

初音未来知识测试网站，使用 .NET 10、ASP.NET Core Blazor Web App、ASP.NET Core Identity、EF Core 和 SQLite。

## 运行

安装 .NET 10 SDK，在项目根目录执行：

```sh
dotnet restore MikuTest.slnx
dotnet build MikuTest.slnx --no-restore
dotnet run --project src/MikuTest.Web --launch-profile http
```

打开 http://localhost:5193。首次启动自动创建数据库和演示题目；后续启动执行兼容升级和幂等初始化。停止服务按 Ctrl+C。

源码不含真实题库、用户账号、答题记录、上传文件或本机配置。新环境会显示演示数据，不会恢复原站点数据。

## 初始化站点所有者

项目没有默认管理员或内置密码。首次启动前，通过进程环境变量或部署平台的密钥设置提供：

| 变量 | 用途 |
| --- | --- |
| `SuperAdminAccount__UserName` | 全新的超级管理员用户名 |
| `SuperAdminAccount__Password` | 自设强密码，至少 8 位，含大小写字母和数字 |
| `SuperAdminAccount__DisplayName` | 可选显示名称 |

已有站点迁移可使用 `SuperAdminAccount__ExistingUserName` 指定启用中的现有管理员；普通账号不能通过该设置直接提升。已有超级管理员时不再初始化，也不会重设密码。初始化成功后移除初始密码环境变量。不要将密码写入配置文件或提交到 Git。

普通用户通过 `/account/register` 注册，超级管理员可在后台任命管理员并逐项授权。当前未提供邮箱验证或密码找回。

## 当前功能

- 预制测试、按领域/标签/难度随机组卷、作答导航、漏答提醒与成绩历史。
- 独立题库、公共阅读材料、文字/图片/音频/视频统一展示，选项可附图。
- 题目和测试多对多复用；测试编排支持预览、拖拽、置顶/置底及整数分值自动保存。
- 题库和编排页支持搜索、筛选与分页；新建测试为草稿，上架前完成编排，下架后才能修改。
- 用户主页、昵称与密码修改、个人历史；历史保存作答时的题干、选项、媒体和解析快照。
- User / Admin / SuperAdmin 身份、细粒度权限、账号禁用与软删除、注册开关、公告及审计日志。
- 浅色/深色/系统主题、响应式布局、管理侧栏与操作反馈。

考察领域对应《初音未来概论》第二至第八章，与标签分开维护，一道题可以涉及多个领域。当前题型均为单选。

下架测试保留答题历史；**删除测试会永久删除该测试的全部答题历史**，独立题目不受影响。删除题目后，历史页会提示该题已删除。账号软删除保留账号与历史，不释放用户名。

## 目录

```text
MikuTest.slnx
src/MikuTest.Web/
  Components/       Blazor 页面、布局及共享组件
  Pages/Account/    Identity 登录、注册、修改密码
  Models/           题目、题集、题组、分值、历史快照模型
  Data/             DbContext、兼容升级与演示数据
  Services/         业务校验、评分、媒体上传、权限及审计
  wwwroot/          样式、脚本及自制演示媒体
  App_Data/         运行时数据库与上传文件（不提交）
  Program.cs        服务注册和启动流程
tests/MikuTest.SmokeTests/  使用临时 SQLite 的集成检查
```

## 数据与媒体

题库和审计保存在 `App_Data/mikutest.db`，账号、设置及身份审计保存在 `App_Data/identity.db`。数据库路径相对 Web 项目根目录，可通过 `ConnectionStrings__Quiz`、`ConnectionStrings__Identity` 覆盖。部署须持久化并备份整个 `App_Data`；升级已有站点前先备份。

上传文件位于 `App_Data/uploads`，通过 `/uploads/` 访问。图片处理后最多 2 MB、音频 5 MB、视频 15 MB，每题最多 5 个媒体。静态图片在浏览器端转换为 WebP 并等比例缩小，动画保留原格式。服务端验证扩展名、大小和文件头，使用随机文件名。为保留历史引用，删除题目不会自动清理文件。媒体链接可公开读取，不要上传私密内容。

仓库仅附带自制数字 SVG、合成提示音和短动画，用于测试媒体渲染；不包含本机替换的插画或封面。已有本地素材保留在原目录，不进入版本控制。

## 验证与代码格式

```sh
dotnet build MikuTest.slnx
dotnet run --project tests/MikuTest.SmokeTests --no-build
dotnet tool restore
dotnet csharpier check .
```

测试使用临时数据库和虚构账号，不读取真实站点数据。格式化 C# 和项目文件使用 `dotnet csharpier format .`；自有 CSS、JavaScript 和 JSON 使用 `pnpm dlx prettier@3.9.9 --write` 加文件路径。不要格式化第三方 `wwwroot/lib`。Razor 中的文本空白可能影响显示，修改后需要页面复查。

## 后续方向

- 将兼容升级脚本逐步迁移到 EF Core migrations，并增加升级回滚演练。
- 增加数据库分页、并发编辑冲突检测、媒体引用清理和对象存储。
- 完善密码找回、部署 HTTPS、备份恢复和持续集成。
- 增加多选等题型及按考察领域的学习统计。
