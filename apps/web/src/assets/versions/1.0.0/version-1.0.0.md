# 版本 1.0.0 發行說明

### 核心功能

- **企業級 AppShell 版面**: 整合標準頂部導航欄與響應式側邊導覽選單。
- **CIM SSO 認證整合**: 支援 Single Sign-On 憑證驗證與本地 MockAuth 開發模式。
- **動態執行階段配置**: 透過 `assets/config.json` 於啟動時動態載入環境參數。
- **版本自動偵測與更新提示**: 登入時自動檢測新版本並提供更新歷程檢視。

### 技術規格

- 採用 Angular 21 Standalone Components 與 Signal 響應式狀態管理。
- 採用純 Tailwind CSS 與原生 HTML5，無外部 Material UI 依賴。
- 遵循 Innolux CIM 企業開發規範。
