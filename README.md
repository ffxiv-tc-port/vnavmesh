# vnavmesh

自動建構地圖導航網格（navmesh）並提供自動尋路移動，同時開放 IPC 供其他插件（如 TextAdvance、Questionable）呼叫。

## 功能

- 依進入的地圖自動建構 / 載入導航網格
- 地面尋路移動、飛行區域的體素（voxel）尋路移動
- 針對特定副本／地圖的碰撞資料修正檔，改善部分地圖的尋路品質
- DTR 狀態列顯示目前尋路狀態
- 提供 IPC 供其他插件呼叫尋路與移動功能
- 除錯／視覺化工具（進階使用者：檢視 navmesh、碰撞體、體素地圖等）

## 指令

`/vnav`（或舊稱 `/vnavmesh`）：
`reload`、`rebuild`、`moveto`、`movedir`、`movetarget`、`moveflag`、
`flyto`、`flydir`、`flytarget`、`flyflag`、`stop`、`aligncamera`、`dtr`

## 安裝

在 Dalamud 設定的「自訂插件庫」加入
`https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json` 並啟用，
再從插件列表安裝。

## 作者

原作 [awgil](https://github.com/awgil/ffxiv_navmesh)。
本分支為 [ffxiv-tc-port](https://github.com/ffxiv-tc-port) 針對台服官方繁中版維護的移植版。
