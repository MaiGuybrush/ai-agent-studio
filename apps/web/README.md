# AiAgentStudioWeb (Angular Enterprise Template 2026)

本專案為企業級 Angular 21 標準範本，整合 Tailwind CSS、企業 SSO 認證（`@inx/inx-auth-lib`）、ESLint 與 Vitest 單元測試框架。

---

## 快速建立新專案 (Scaffolding)

本專案提供 PowerShell 腳手架腳本 [`scaffold.ps1`](./scaffold.ps1)，讓開發者能直接透過參數 `<AppName>` 從 Gitea 克隆本範本，自動完成識別名稱替換、重置乾淨的 Git 歷史紀錄，並選擇性安裝相依套件。

### 腳手架執行步驟說明

當執行 `scaffold.ps1` 時，腳本會自動依序執行以下 5 個步驟：
1. **克隆範本**：從 Gitea 企業倉儲克隆 `angular-template-2026` 範本至目標路徑（若使用 `-InPlace` 則跳過）。
2. **清除舊歷史**：移除原本範本的 `.git` 目錄，確保新專案擁有獨立乾淨的版本控管起點。
3. **替換識別名稱**：
   - 將專案名稱轉換為 Kebab-case（如 `my-new-app`）與 PascalCase（如 `MyNewApp`）。
   - 自動更新 `package.json`、`angular.json`、`src/index.html`、`src/app/app.ts`、`src/assets/config.json` 等檔案內的名稱與標題。
   - 重置 Conductor 任務軌道資料，並在新專案中自動清理 `scaffold.ps1` 腳手架腳本。
4. **重新初始化 Git**：執行 `git init`，並建立首個提交（Initial commit）。
5. **安裝相依套件**：自動偵測並執行 `pnpm install`（若指定 `-SkipInstall` 則跳過）。

---

### 參數說明

| 參數名稱 | 類型 | 必要 | 預設值 | 說明 |
| :--- | :--- | :---: | :--- | :--- |
| `-AppName` | 字串 | **是** (Position 0) | - | 新專案名稱（支援英數字、連字號與底線，例如 `my-new-app` 或 `MyNewApp`） |
| `-TargetDir` | 字串 | 否 (Position 1) | `./<AppName>` | 目標產出目錄路徑（支援相對路徑或絕對路徑） |
| `-TemplateUrl` | 字串 | 否 | `http://tncimweb.cminl.oa/git-server/guy.mai/angular-template-2026.git` | 範本來源 Gitea 倉儲位址 |
| `-Branch` | 字串 | 否 | `master` | 欲克隆的範本分支，同時也是新專案初始化的預設分支 |
| `-SkipInstall` | 開關 (Switch) | 否 | `$false` | 略過自動執行 `pnpm install`（適合離線環境或 CI 流程） |
| `-InPlace` | 開關 (Switch) | 否 | `$false` | 就地模式：直接在當前目錄更名，不執行 git clone 且保留腳手架腳本 |

---

### 常見使用範例

#### 1. 基本用法（推薦）
在當前目錄下建立新專案目錄，自動克隆並安裝套件：
```powershell
.\scaffold.ps1 -AppName "mes-defect-dashboard"
```
> 亦可使用位置參數簡寫：
> ```powershell
> .\scaffold.ps1 "mes-defect-dashboard"
> ```

#### 2. 自訂目標目錄與略過安裝
指定自訂路徑，並略過 `pnpm install`（稍後手動安裝）：
```powershell
.\scaffold.ps1 -AppName "mes-defect-dashboard" -TargetDir "D:\Projects\factory-apps\mes-defect-dashboard" -SkipInstall
```

#### 3. 指定範本分支或自訂 Git 來源
若需基於特定開發分支建立專案：
```powershell
.\scaffold.ps1 -AppName "mes-defect-dashboard" -Branch "develop"
```

#### 4. 當前專案就地更名（In-Place）
若已手動下載或複製本專案，欲直接將當前目錄更名為新專案：
```powershell
.\scaffold.ps1 -AppName "mes-defect-dashboard" -InPlace
```

---

### 建立完成後的下一步

腳手架執行完成後，請依提示進入新專案開始開發：

```powershell
# 1. 進入新專案目錄
cd .\mes-defect-dashboard

# 2. 若先前使用了 -SkipInstall，請手動安裝相依套件
pnpm install

# 3. 啟動本地開發伺服器
pnpm start

# 4. 關聯至您的企業 Gitea 新倉儲並推送
git remote add origin http://tncimweb.cminl.oa/git-server/<your-account>/<your-repo>.git
git push -u origin master
```

---

## 開發指南 (Development)

### 啟動本地開發伺服器

```bash
pnpm start
# 或 ng serve
```

預設將啟動於 `http://localhost:4200/`。

### 建置專案

```bash
pnpm build
```

### 執行單元測試

```bash
pnpm test
```

### 程式碼檢查與排版

```bash
pnpm lint
pnpm format
```

---

## 架構與核心功能

- **執行時期設定 (RuntimeConfig)**：啟動時由 `assets/config.json` 動態載入，支援相對路徑與絕對路徑 API 位址，無需重新編譯即可切換環境。
- **單一登入與模擬機制 (SSO & MockAuth)**：支援企業級 SSO 重新導向認證，本機離線開發可透過 `mockAuth: true` 啟用合成使用者身份（`mockUser`）。
- **企業版面外殼 (AppShell)**：提供整合員工資訊（`EmpInfo`）、廠區情境（`ShopContext`）與響應式可折疊側邊欄的標準應用程式版面。
