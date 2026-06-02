# tia 命令行 —— 5 分钟上手

v2.0 起，交付包里的 **同一个 exe** 既是 MCP 服务，也是命令行 `tia`。
不需要装 MCP 客户端、不需要会编程：**任意 AI 写一份 spec，你跑一条命令。**

可执行文件：`tools/tiaportal-mcp/src/TiaMcpServer/bin/Release/net48/TiaMcpServer.exe`
（下文用 `tia` 指代它。）

---

## 三种用法，门槛从低到高

### 1. 最简单：双击 .bat
- 把一个 `spec.yaml` 或 `spec.json` **拖到 `scripts\生成工程.bat` 上** → 自动建工程。
- 想让连接更快：先双击 `scripts\预热.bat`（留着别关），之后每次建工程约 1 秒连上。

### 2. 一条命令
```
tia gen  项目.yaml              # 从 spec 建完整工程
tia gen  项目.yaml --dry-run    # 只离线校验 spec，不连 TIA、不建任何东西
tia patch 改动.yaml             # 把 spec 增量合并进已有工程（spec 里写 projectPath）
tia compile  D:\proj\X.ap21 --plc PLC_1
tia describe D:\proj\X.ap21 --plc PLC_1
tia prewarm                     # 常驻 headless 实例，后续命令 ~1s 连上
tia schema                      # 打印 spec 所有字段说明
```
退出码：**0=成功，1=有失败步骤，2=错误**（方便脚本/CI 判读）。
加 `--json` 输出机器可读结果，方便让 AI 读结果自我纠错。

### 3. 让 AI 生成 spec
把 `docs/AI_spec_prompt.md` 里的提示词 + `tia schema` 的输出贴给任意 AI，
描述你要的工程，AI 产出 `spec.yaml`，再走用法 1 或 2。

---

## spec 长什么样

最小例子（YAML）：
```yaml
projectName: MyLine
plcName: PLC_1
plcFamily: S7-1500
udt:
  - name: UDT_Status
    members:
      - { name: Active, datatype: Bool, commentZhCn: 运行 }
tagTable:
  - tableName: IO
    tags:
      - { name: Start, dataTypeName: Bool, logicalAddress: "%I0.0" }
compile: true
save: true
```
完整字段见 `tia schema`，现成模板见 `templates/project-blueprints/`（启停、电机，均编译 0 错）。

提示：
- `tia gen` 从零建；`tia patch` 改已有工程（spec 里加 `projectPath: D:\...\X.ap21`）。
- HMI 画面 `width/height` 要按面板原生分辨率，否则被裁剪。
- `hmiTags` 用绝对地址（`%M..`）更容易通过回读校验。
- JSON 是首选格式（零歧义，AI 生成最稳）；YAML 是给人读写的便利。

---

## 常见问题

- **慢？** 第一次连接冷启动 headless TIA 约 10–28s；先 `tia prewarm` 之后约 1s。再快不了——`CreateProject/AddDevice/Save` 是 Openness 固有耗时。
- **中文乱码？** 输出已强制 UTF-8；`.scl` 文件请存 UTF-8 BOM。
- **V20 还是 V21？** 用与你 TIA 大版本匹配的那个 exe（`bin\Release`=V21，`bin-v20\Release`=V20）。可加 `--tia-major-version 20|21` 或 `--tia-portal-location <安装根>` 覆盖。
- **要看 GUI？** 加 `--with-ui` 用完整界面启动（较慢）。
