# StemStudio 架構與封裝技術說明

## 1. 產品目標與範圍

StemStudio 是 Windows 10/11 x64 本機 AI 樂曲分軌桌面程式。使用者拖放或選擇音訊檔、選擇要輸出的音軌，程式在本機推論並輸出 WAV。第一階段支援 vocals、guitar、bass、drums，不包含帳號、雲端音訊上傳、歌詞或評分。

## 2. 模型決策

第一版使用 **Demucs v4 `htdemucs_6s`**。

- 六來源模型提供 vocals、drums、bass、guitar、piano、other；UI 提供其中四種選擇。
- Guitar 是實驗性來源，品質會受歌曲編曲、混音及其他樂器遮蔽影響；有些內容可能分到 `other` 或相鄰音軌。
- 標準 `htdemucs` 四來源模型沒有 guitar stem，不符合功能需求，所以目前使用 `htdemucs_6s`。
- 推論使用 CPU 版 PyTorch，避免要求特定 NVIDIA/CUDA 驅動。模型權重約 55 MB，不內嵌於安裝檔，首次使用時經使用者確認後下載。

## 3. 專案模組與依賴

```text
StemStudio.sln
├── src/
│   ├── StemStudio.App/             WPF UI、使用者互動與處理流程
│   ├── StemStudio.Core/            分軌請求/結果、設定與服務介面
│   ├── StemStudio.Infrastructure/  設定檔、程序啟動、檔案輸出
│   └── StemStudio.Worker/          獨立 .NET worker host
├── backend/
│   ├── demucs_worker.py            Python 模型管理與 Demucs 推論
│   └── requirements.txt            Python 推論套件版本
├── installer/
│   ├── build-installer.ps1         runtime、App、worker 與 setup 的建置流程
│   └── StemStudio.iss              Inno Setup 安裝規格
└── docs/
    └── ARCHITECTURE.md
```

| 專案 | 技術與責任 | 不負責的工作 |
| --- | --- | --- |
| `StemStudio.App` | WPF / .NET 8；拖放、選檔、勾選音軌、下載同意、磁碟提示、設定 UI | Python、Torch、Demucs 命令列 |
| `StemStudio.Core` | 純 .NET 型別與服務契約：`SeparationRequest`、`SeparationResult`、`SeparationStem`、設定及服務介面 | WPF、外部程序、檔案 I/O |
| `StemStudio.Infrastructure` | 實作分軌與設定服務；啟動 worker、轉送狀態、處理取消/錯誤、檢查輸出 | 視窗元件、模型張量 |
| `StemStudio.Worker` | 獨立自包含 .NET 程序；驗證啟動參數，啟動指定的 Python 程序並轉送輸出 | UI 與使用者設定畫面 |
| `backend/demucs_worker.py` | Python worker；模型狀態/準備、設定模型快取、呼叫 Demucs、整理來源 WAV | 呼叫使用者全域 Python、WPF |

依賴方向為 `App → Core`、`App → Infrastructure`、`Infrastructure → Core`。App 依賴 Core 的模型無關介面；Infrastructure 將服務呼叫轉成程序命令，避免在 WPF 程序載入 Python/PyTorch。

## 4. 執行時程序架構

```text
┌──────────────────────────────────────────┐
│ StemStudio.App.exe                       │
│ WPF UI + Core + Infrastructure           │
└────────────────────┬─────────────────────┘
                     │ ProcessStartInfo.ArgumentList
                     │ stdout: STATUS|... / stderr: error details
                     ▼
┌──────────────────────────────────────────┐
│ StemStudio.Worker.exe (.NET self-contained)│
└────────────────────┬─────────────────────┘
                     │ 指定安裝目錄內的 python.exe
                     │ + backend\demucs_worker.py
                     ▼
┌──────────────────────────────────────────┐
│ CPython 3.12 + PyTorch CPU + Demucs 4.1  │
│ Demucs htdemucs_6s                       │
└──────────────────────────────────────────┘
```

### 命令與程序通訊

Infrastructure 使用 `ProcessStartInfo.ArgumentList` 一項一項傳遞引數，避免路徑含空白時的 shell quoting 問題。worker 命令分三種：

```text
StemStudio.Worker.exe model-status --python <private-python.exe> --script <worker.py> --model-cache <cache-dir>
StemStudio.Worker.exe prepare-model --python <private-python.exe> --script <worker.py> --model-cache <cache-dir>
StemStudio.Worker.exe separate --input <audio> --output <folder> --stems vocals,guitar --python <private-python.exe> --script <worker.py> --model-cache <cache-dir>
```

- Worker 將 Python stdout/stderr 逐行轉送為 `STATUS|...`；Infrastructure 解析狀態前綴並更新 UI。Python traceback 仍透過 stderr 回傳，worker 以非零 exit code 表示失敗。
- 模型檢查另外輸出 `STATUS|MODEL_READY|true/false`，App 只在收到 `true` 時進入可分軌狀態。
- 取消 token 取消 App 等待並終止 worker 的整個程序樹；Demucs Python 子程序也會停止。
- 每個工作以 exit code 和預期輸出檔案是否存在共同判斷成功；若失敗，清除該工作的空/部分輸出目錄並顯示錯誤。

## 5. 分軌資料流

1. WPF 接收檔案拖放或多選；App 檢查檔案存在與副檔名、避免同一檔案重複加入。
2. 使用者勾選 vocals/guitar/bass/drums，App 將輸入路徑和音軌清單組成 Core `SeparationRequest`。
3. App 啟動 worker。正式安裝使用 `{app}\python\python.exe` 和 `{app}\backend\demucs_worker.py`；只有 Debug 開發版本能透過 `STEMSTUDIO_PYTHON` 指定本機 Python 環境。
4. Python worker 把模型快取環境變數指向使用者設定的位置，並呼叫 `python -m demucs.separate --name htdemucs_6s`。
5. Demucs 將完整來源先寫入 `%LOCALAPPDATA%\StemStudio\staging` 下的唯一暫存目錄。
6. Python worker 只把使用者勾選的 WAV 複製到輸出目錄；成功後清除暫存。Infrastructure 檢查要求的 WAV 都存在，建立 Core `SeparationResult`。

輸入格式：WAV、MP3、FLAC、M4A、OGG、WMA、AAC。所有輸出均為 WAV。Demucs 會計算模型的來源，勾選只控制哪些 WAV 會複製並保留，不會縮減模型推論所需計算量。

## 6. 首次啟動、模型快取與設定

### 模型下載流程

1. WPF Loaded 時啟動 `model-status`，此時 UI 先停用分軌與設定操作，避免初始化和使用者工作互相競爭。
2. 若模型尚未就緒，App 顯示模型名稱、約 55 MB 下載量、模型快取路徑與該磁碟可用空間，建議預留至少 1 GB 給快取及輸出。
3. 使用者同意後才呼叫 `prepare-model`。Demucs 4.1 透過 Hugging Face 取得模型定義及 safetensors 權重。
4. 完成載入後，Python worker 在快取根目錄寫 `.stemstudio-model-ready.json`。後續狀態檢查要求 marker、模型 YAML 及非空的 safetensors 權重檔皆存在。
5. 使用者選擇另一個快取目錄時，該目錄視為另一份模型快取，需要重新下載。

worker 設定下列程序環境變數；不使用預設的全域 Torch/Hugging Face cache：

```text
TORCH_HOME=<model-cache>
HF_HOME=<model-cache>\huggingface
HF_HUB_CACHE=<model-cache>\huggingface\hub
PYTHONUTF8=1
```

### 使用者資料位置

| 資料 | 預設位置 |
| --- | --- |
| App 設定 JSON | `%LOCALAPPDATA%\StemStudio\settings.json` |
| 模型與 Hugging Face cache | `%LOCALAPPDATA%\StemStudio\models` |
| Demucs 暫存 | `%LOCALAPPDATA%\StemStudio\staging` |
| 分軌 WAV | Windows 使用者「音樂\StemStudio」資料夾 |
| App / worker / Python | 安裝目錄，預設 `%LOCALAPPDATA%\Programs\StemStudio` |

設定視窗可變更輸出與模型 cache 位置。解除安裝只刪除安裝目錄，不刪除使用者設定、下載的模型與輸出音訊。

## 7. 安裝包如何建置

入口腳本是 `installer/build-installer.ps1`，安裝內容由 `installer/StemStudio.iss` 定義。整個流程在建置機上下載/安裝相依，不會在使用者安裝過程中執行 pip。

### Payload 建置步驟

1. **建立私有 CPython runtime**
   - 下載 CPython 3.12.10 x64 embeddable ZIP。
   - 解壓到 `artifacts\installer\payload\python`。
   - 修改 `python312._pth` 加入 `Lib\site-packages` 並啟用 `import site`，讓 embeddable runtime 可載入套件。
   - 下載 `get-pip.py`，在這份私有 Python 內安裝 pip；不修改系統 Python、PATH 或 registry。
2. **安裝 Python 推論依賴**
   - `backend/requirements.txt` 將 PyTorch CPU wheel index 設為套件來源。
   - 安裝直接鎖定的 Demucs 4.1.0、Torch 2.5.1+cpu、torchaudio 2.5.1+cpu、NumPy 1.26.4。
   - 以私有 Python 匯入 `demucs.separate` 和 `torch`，並驗證 Torch 版本，確保 payload 不只是成功複製套件。
3. **發佈 .NET 執行檔**
   - WPF App 發佈為 `win-x64` self-contained single-file EXE，內含 App 所需 .NET runtime。
   - Worker 專案獨立發佈成 `win-x64` self-contained single-file EXE，App 不需要使用者安裝 .NET 或依賴 `dotnet` 指令。
   - Python worker 腳本複製到 payload 的 `backend` 子目錄。
4. **產生安裝程式**
   - Inno Setup 6 讀取 `.iss` 規格，把 App EXE、worker EXE、完整 private Python 目錄及 worker 腳本一併壓縮。
   - 產生 `artifacts\installer\StemStudio-Setup.exe`，安裝到目前使用者的 Programs 目錄，建立開始功能表捷徑，提供選擇性的桌面捷徑。

模型權重故意不放入 installer。這樣安裝包不會再包含另一份 checkpoint，也允許首次啟動時顯示下載資訊，使用者可選擇下載位置。

### 建置指令與產物

建置機需求：Windows x64、.NET 8 SDK、網路、足夠下載/暫存空間，以及 Inno Setup 6。

#### 在 Visual Studio 單獨發佈 App

若只修改 WPF UI，無須重建完整 Python runtime 或安裝程式。Visual Studio 可在方案總管對 `StemStudio.App` 選 **Publish**，使用 `FolderProfile` 發佈；設定檔固定為 Release、`win-x64`、self-contained、single-file，輸出到：

```text
src\StemStudio.App\bin\Release\net8.0-windows\publish\
```

此資料夾中的 `StemStudio.App.exe` 是可單獨部署的 self-contained 單檔（約 154 MB，大小會隨版本改變）。關閉 App 後，可只用它覆蓋安裝目錄中的 `{app}\StemStudio.App.exe`。保留安裝目錄原有的 `StemStudio.Worker.exe`、`python` 和 `backend`；不要將一般 Build 的 `bin\Release\net8.0-windows\StemStudio.App.exe` 當成此單檔替代品。一般 Build 輸出是小型 apphost，仍依賴旁邊的 .NET assemblies、runtime 設定及 .NET runtime，單獨替換安裝版會無法啟動。

FolderProfile 只發佈 App，不會更新 Worker、Python runtime 或 `backend\demucs_worker.py`。若同時修改 Python worker，將來源 `backend\demucs_worker.py` 另外複製到安裝目錄的 `backend` 子目錄。若修改 Worker 或 Python runtime，應使用完整安裝包流程重新發佈和安裝；不要拿 App 發佈資料夾裡可能殘留的 Worker/相依檔覆蓋安裝目錄。若要交付完整一致的版本，仍以完整安裝包建置為準。

```powershell
.\installer\build-installer.ps1
```

- `-SkipRuntimeBuild`：重用已存在的 private Python payload；仍會重新安裝/驗證 requirements、發佈 App/worker 並編譯 installer。
- `-SkipInstallerCompile`：建置並驗證 payload，但略過 Inno Setup 編譯。
- `-InnoSetupCompiler <path>`：明確指定未自動偵測到的 `ISCC.exe`。
- 建置產物在 `artifacts\installer`，整個 `artifacts` 已加入 `.gitignore`，不會把約 220 MB 的 installer 或 runtime 併入 Git 原始碼。

目前直接依賴有固定版本；requirements 尚未包含所有傳遞相依的 hash/完整 lockfile，所以不同日期建置可能解析到不同的間接依賴。若要做可重現或正式對外發佈，應產生完整 lockfile、記錄 runtime/download hash 並完成第三方 license/notice 審查。

## 8. 使用者安裝流程

1. 執行 `StemStudio-Setup.exe`，選擇安裝位置；預設為 `%LOCALAPPDATA%\Programs\StemStudio`，不需要系統管理員權限。
2. Setup 將 App、worker、CPython/PyTorch/Demucs runtime 安裝到同一個目錄樹，並建立捷徑；安裝過程不需要網路下載 Python 套件或模型。
3. 啟動 App 時，App 使用自己的安裝目錄定位 `StemStudio.Worker.exe`、`python\python.exe` 及 `backend\demucs_worker.py`；找不到必要檔案時顯示修復安裝錯誤，不回退呼叫全域 Python。
4. 如果模型 cache 尚無模型，App 詢問使用者是否下載；下載到可設定的 LocalAppData cache。
5. 分軌完成後 WAV 寫入輸出資料夾；解除安裝 App 時，保留使用者設定、cache 及輸出音訊。

## 9. 測試與限制

- Release 解決方案可建置；Python worker 有語法檢查。
- 已使用安裝包內的 private Python 和 worker 實際檢查/下載模型、執行 Demucs，並用 CC0 吉他曲目驗證四種 stem 輸出。
- 已用 Inno Setup 編譯，並在測試目錄驗證安裝、安裝後 worker 啟動及解除安裝。
- 已測的吉他曲目為吉他主奏，不代表完整樂團混音的串音品質；仍需評估含鼓、貝斯、人聲的混音素材。
- 正式散布前需完成 Demucs、PyTorch、Python、各相依套件及模型權重的 license/再散布條款審查。

## 參考

- [Demucs](https://github.com/adefossez/demucs)：分軌模型與命令列介面。
