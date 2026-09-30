# SotF Client Probe（C 端采集插件）— 源码发布版

> 版本：**0930BETA（插件 A1.09）**
> 游戏：Sons Of The Forest（Steam AppID 1326470）
> 框架：BepInEx 6（IL2CPP）/ .NET 6 / Unity 2022.2
> 本目录为**脱敏发布版源码**：服务器地址、SteamID、内部路径均已替换为 `<SERVER_IP>`、`<YOUR_STEAMID64>` 等占位符。

## 项目简介

Sons Of The Forest 专用服务器生态的**客户端（C 端）数据采集插件**：

- 采集玩家会话数据：坐标、SteamID+昵称、在线时长、移动距离、伤害输出、击杀数、死亡次数与死亡评级、弹反/格挡/闪避统计、血量损失/回复等
- **HUD 覆盖层**：左侧显示 SID / HP / DIS / DMG / KILLS / BlK / DIE / KD / PAR / Season / 世界时间 / 在线时长
- **数据链路**：C 端 → 子服务器 center（HTTP JSON，内存操作）→ 主 center 聚合入库，排行榜按 KD 累计（玩家不可抹除历史表现）
- **禁止物资清单校验**：清除玩家背包中的违禁物品（手雷/C4/粘性炸弹/枪械/弹药/高等级护甲等，通关必需的金色护甲不在名单内）
- **屏幕接管**：电影院 / 大屏 / 显示器播放 001-003 视频与音频（5M/20M/50M 距离触发，被动式，不主动轮询）
- **反作弊**：仅数据采集与上报，不含对玩家的惩罚执行（拒绝服务由服务端裁定）

## 目录结构

```
src/                  C# 源码（29 个文件，BepInEx 插件）
SotfClientProbe.csproj  项目文件（编译需 -p:SotfDir=<游戏目录>）
build.ps1             编译 + 部署 + 校验脚本
install.ps1           安装脚本
README.md             开发版说明（已脱敏）
A1_CHANGES.txt        变更记录（已脱敏）
assets/               资源（logo 等）
```

## 编译

```powershell
# 需要：.NET 6 SDK、游戏已至少启动一次（生成 IL2CPP interop）
dotnet build SotfClientProbe.csproj -c Release -p:SotfDir="C:\Steam\steamapps\common\Sons of the Forest"
```

产物 `bin\SotfClientProbe.dll` 复制到游戏目录 `BepInEx\plugins\` 即可（连同 `winhttp.dll`、`doorstop_config.ini` 一起部署）。

## 部署要点

1. 游戏目录放入 `winhttp.dll` + `doorstop_config.ini`（BepInEx 入口）+ `BepInEx\` 目录
2. `plugins\` 目录**只能有本插件**（反作弊规则：存在其他插件时探针自动失效）
3. 首次启动生成 `BepInEx\interop\`（约 1 分钟）
4. 进认证服（专用服务器，带圆点标志）后 HUD 生效

## 服务器配置

连接地址由代码从 Bolt 连接自动推导，发布版已替换为占位符 `<SERVER_IP>`；实际部署时需将 `src/Link.cs`、`src/BoltProbe.cs` 中的占位替换为你的 center 地址与端口。

## 免责声明

- 本插件仅用于自有服务器生态的数据采集与排行榜，不包含任何对玩家的惩罚执行逻辑
- 请勿用于官方服务器或未经授权的环境
- 使用产生的账号/封禁风险由使用者自行承担
- 源码仅供参考与学习；二次开发请遵守游戏服务条款
