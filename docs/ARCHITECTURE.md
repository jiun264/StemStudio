# StemStudio 系統架構與開發說明

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
    ├── ARCHITECTURE.md             執行時架構、資料流與開發除錯
    └── 安裝包架構.md                runtime、publish 與安裝包建置
```

| 專案 | 技術與責任 | 不負責的工作 |
| --- | --- | --- |
| `StemStudio.App` | WPF / .NET 8；拖放、選檔、勾選音軌、下載同意、磁碟提示、設定 UI | Python、Torch、Demucs 命令列 |
| `StemStudio.Core` | 純 .NET 型別與服務契約：`SeparationRequest`、`SeparationResult`、`SeparationStem`、設定及服務介面 | WPF、外部程序、檔案 I/O |
| `StemStudio.Infrastructure` | 實作分軌與設定服務；啟動 worker、轉送狀態、處理取消/錯誤、檢查輸出 | 視窗元件、模型張量 |
| `StemStudio.Worker` | 獨立自包含 .NET 程序；驗證啟動參數，啟動指定的 Python 程序並轉送輸出 | UI 與使用者設定畫面 |
| `backend/demucs_worker.py` | Python worker；模型狀態/準備、設定模型快取、呼叫 Demucs、整理來源 WAV | 呼叫使用者全域 Python、WPF |

依賴方向為 `App → Core`、`App → Infrastructure`、`Infrastructure → Core`。App 依賴 Core 的模型無關介面；Infrastructure 將服務呼叫轉成程序命令，避免在 WPF 程序載入 Python/PyTorch。

安裝包建置、publish 選項與安裝流程另見 [安裝包架構](安裝包架構.md)。

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

Infrastructure 使用 `ProcessStartInfo.ArgumentList` 一項一項傳遞引數，避免路徑含空白時的 shell quoting 問題。worker 命令如下：

```text
StemStudio.Worker.exe model-status --python <private-python.exe> --script <worker.py> --model-cache <cache-dir>
StemStudio.Worker.exe prepare-model --python <private-python.exe> --script <worker.py> --model-cache <cache-dir>
StemStudio.Worker.exe download-audio --url <http(s)-url> --output <folder> --python <private-python.exe> --script <worker.py> --model-cache <cache-dir>
StemStudio.Worker.exe separate --input <audio> --output <folder> --stems vocals,guitar --python <private-python.exe> --script <worker.py> --model-cache <cache-dir>
```

- Worker 將 Python stdout/stderr 逐行轉送為 `STATUS|...`；Infrastructure 解析狀態前綴並更新 UI。Python traceback 仍透過 stderr 回傳，worker 以非零 exit code 表示失敗。
- 模型檢查另外輸出 `STATUS|MODEL_READY|true/false`，App 只在收到 `true` 時進入可分軌狀態。
- 取消 token 取消 App 等待並終止 worker 的整個程序樹；Demucs Python 子程序也會停止。
- 每個工作以 exit code 和預期輸出檔案是否存在共同判斷成功；若失敗，清除該工作的空/部分輸出目錄並顯示錯誤。

## 5. 開發除錯工具與流程

### 開發工具

| 工具 | 用途 |
| --- | --- |
| Visual Studio 2022（.NET 桌面開發工作負載） | 開啟 `StemStudio.sln`、WPF XAML 設計/編譯、F5 啟動、C# 中斷點與 Attach to Process |
| .NET 8 SDK | 建置及執行 App、Core、Infrastructure、Worker |
| Python 3.12 x64 與 Python 開發工具/擴充 | 建立 `.venv`、安裝 Demucs 相依、設定 Python 中斷點與除錯器 |
| Git | 比對來源變更；建置輸出位於 `bin`、`obj`、`artifacts`，不應當作來源修改提交 |

安裝 Python 相依只需在建立或更新開發虛擬環境時執行：

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r .\backend\requirements.txt
```

`.venv` 使用與封裝環境相同的直接依賴版本，但不是安裝版所用的私有 Python。VS Code 請以 Python 擴充選取 `.venv\Scripts\python.exe`；這個選取只控制編輯器的 Python 功能，App 啟動的子程序仍需明確設定 `STEMSTUDIO_PYTHON`。

### Visual Studio 以 F5 除錯 WPF App

1. 在 Visual Studio 開啟 `StemStudio.sln`，將 `StemStudio.App` 設為啟始專案，組態選 `Debug`。
2. 在 App 專案的偵錯啟動設定中，加入環境變數 `STEMSTUDIO_PYTHON`，值為專案完整路徑下 `.venv\Scripts\python.exe`，例如 `C:\work\StemStudio\.venv\Scripts\python.exe`。可從專案右鍵 **Properties → Debug → Open debug launch profiles UI** 編輯；若該 VS 版本不提供環境變數欄位，請在啟動 Visual Studio 前先設定 Windows 使用者環境變數。
3. 按 F5 啟動 WPF App。先在 `MainWindow.xaml.cs` 的 `Window_Loaded`、`PrepareModelIfNeededAsync` 或 `SeparateButton_Click` 設 C# 中斷點，確認 UI 事件、模型狀態、取消與錯誤流程。
4. 在 `DemucsSeparationService.cs` 的 `RunWorkerAsync` 設中斷點，檢查實際選用的 Python 路徑、Worker 路徑、script 路徑、命令列參數與快取路徑。

`STEMSTUDIO_PYTHON` 只在 `Debug` 編譯中讀取；App 會將它傳給 Worker，Worker 再啟動此 Python 與 `backend\demucs_worker.py`。正式安裝／Release 不讀取這個環境變數，而固定使用安裝目錄內的 Python。只在 IDE 的 Python 解譯器清單選擇 `.venv`，不會自動改變 App 子程序使用的 Python。

### 除錯 .NET Worker 子程序

Worker 是獨立程序，App 的 F5 debugger 不會自動逐步進入 Worker。要除錯 `src\StemStudio.Worker\Program.cs`：

1. 以 F5 啟動 App 並開始分軌，趁工作仍在進行時開啟 Visual Studio **Debug → Attach to Process**，選取 `StemStudio.Worker.exe`，確認程式碼類型為 .NET（CoreCLR）。Worker 若已結束，重新開始一次較長的分軌工作再附加。
2. App Debug 組態會產生 Worker 的符號檔；確認 Worker DLL/PDB 與目前原始碼同一次建置，並在 Modules 視窗確認 symbols 已載入，否則中斷點可能呈現未繫結。
3. 需要單獨檢查 Worker 參數時，可將 `StemStudio.Worker` 暫設為啟始專案，在其 Debug 設定提供 `model-status` 或 `separate` 參數以及 `--python`、`--script`、`--model-cache` 路徑。完成後將啟始專案改回 `StemStudio.App`。

### 除錯 Python / Demucs

Worker 會另外啟動 Python 子程序，所以 App 和 Worker 的 C# debugger 不會自動中斷在 Python 程式碼。除錯 `backend\demucs_worker.py` 有兩種方式：

- **先單獨重現（最穩定）**：在 Visual Studio Python 工具或 VS Code Python 擴充中開啟 `backend\demucs_worker.py`，使用 `.venv\Scripts\python.exe` 除錯此檔，並在啟動參數加入下方指令。Python 入口會解析第一個位置參數作為命令。
- **端到端附加**：以 F5 啟動 App 並開始分軌，在 Python 子程序尚未結束時，使用 Python 除錯器 Attach to Process 附加該次新啟動的 `python.exe`。`model-status` 很快就會結束，不適合附加；要測狀態檢查時使用前一種方式。

直接呼叫 worker 的 PowerShell 範例（在專案根目錄執行）：

```powershell
$python = (Resolve-Path .\.venv\Scripts\python.exe).Path
$script = (Resolve-Path .\backend\demucs_worker.py).Path
$cache = Join-Path $env:LOCALAPPDATA 'StemStudio\models'

& $python $script model-status --model-cache $cache
& $python $script prepare-model --model-cache $cache
& $python $script separate --model-cache $cache --input 'C:\audio\sample.wav' --output "$env:TEMP\StemStudio-debug" --stems vocals,guitar
```

`prepare-model` 會下載模型，僅在需要且有網路時執行；`separate` 會實際執行推論並寫 WAV。使用 App 時取消工作會終止 Worker 與整個 Python 子程序樹；直接執行 Python 時則由除錯器控制。不要為了除錯刪除正式模型快取；可在命令中將 `$cache` 改指向單獨的測試資料夾。

#### 單獨測試網址下載

若 App 的「下載 MP3」失敗，可先直接呼叫 Python worker，將問題範圍縮小到 yt-dlp、FFmpeg、網路或 Python 腳本，而不經過 WPF 與 .NET Worker。以下範例在專案根目錄執行，使用安裝包建置目錄中的 Python runtime，並直接測試目前 checkout 的 worker 腳本：

```powershell
$python = (Resolve-Path .\artifacts\installer\payload\python\python.exe).Path
$script = (Resolve-Path .\backend\demucs_worker.py).Path
$url = 'https://www.youtube.com/watch?v=<video-id>'
$testRoot = Join-Path $env:TEMP ('StemStudio-urltest-' + [guid]::NewGuid().ToString('N'))
$output = Join-Path $testRoot 'output'
$cache = Join-Path $testRoot 'cache'

New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
& $python -c "import yt_dlp, imageio_ffmpeg; print('yt-dlp', yt_dlp.version.__version__); print('FFmpeg', imageio_ffmpeg.get_ffmpeg_exe())"
if ($LASTEXITCODE -ne 0) { throw 'Python runtime 或 yt-dlp/FFmpeg 依賴無法使用。' }

& $python $script download-audio --model-cache $cache --url $url --output $output
if ($LASTEXITCODE -ne 0) { throw "download-audio 失敗，結束碼：$LASTEXITCODE" }
Get-Item (Join-Path $output 'audio.mp3') | Select-Object FullName, Length
```

將 `<video-id>` 換成有權下載、且來源允許下載的公開影片 ID。此命令會實際連線下載並轉成 MP3；成功時會顯示輸出檔案路徑及大小。測試檔會留在 `$testRoot` 供檢查，確認後可用 `Remove-Item -LiteralPath $testRoot -Recurse -Force` 清除。`download-audio` 雖不載入 Demucs 模型，仍須提供 `--model-cache`，因為 Python worker 的共用命令列入口要求此參數。

若要測試安裝包實際帶的腳本與 Python，將 `$python` 和 `$script` 分別改為安裝目錄下的 `python\python.exe` 與 `backend\demucs_worker.py`。若只把 Python 指向 `artifacts\installer\payload\python\python.exe`，而 `$script` 指向專案 `backend`，則測到的是安裝包 Python 相依套件搭配目前原始碼的組合，並非完整安裝目錄。

若錯誤指出 PATH 中某個目錄產生 WinError 448，可先用上述直接命令確認是否能重現。Worker 的下載流程會在該次 Python 程序內略過無法解析的 PATH 項目並保留其他項目，不會修改 Windows 的使用者或系統 PATH。單獨 Python 測試成功只代表該 Python／腳本／網路組合可下載；若 App 仍失敗，接著檢查 Infrastructure 傳入的 Python、腳本、參數與程序環境。

### 使用 CMD 執行與觀察錯誤

CMD 不提供原始碼中斷點，但可直接啟動程式、重現問題並查看程序結束碼或 Python traceback。以下命令在專案根目錄執行；請先確認 `.venv` 已建立並安裝 `backend\requirements.txt`。

```bat
cd /d C:\work\StemStudio
set "STEMSTUDIO_PYTHON=%CD%\.venv\Scripts\python.exe"
dotnet run --project .\src\StemStudio.App\StemStudio.App.csproj --configuration Debug
```

`set` 只對目前 CMD 視窗及由它啟動的 App 生效。若要在 CMD 直接重現 Python worker，先設定路徑，再執行：

```bat
cd /d C:\work\StemStudio
set "PYTHON=%CD%\.venv\Scripts\python.exe"
set "SCRIPT=%CD%\backend\demucs_worker.py"
set "CACHE=%LOCALAPPDATA%\StemStudio\models"
"%PYTHON%" "%SCRIPT%" model-status --model-cache "%CACHE%"
"%PYTHON%" "%SCRIPT%" separate --model-cache "%CACHE%" --input "C:\audio\sample.wav" --output "%TEMP%\StemStudio-debug" --stems vocals,guitar
echo Exit code: %ERRORLEVEL%
```

`model-status` 不會分軌；`separate` 會實際推論並在指定輸出資料夾寫 WAV。命令結束前可在視窗直接讀取錯誤訊息。CMD 只適合執行和觀察輸出；需要逐行中斷點請使用 Visual Studio 或 VS Code debugger。

### 在 VS Code 執行與除錯

1. 在 VS Code 開啟包含 `StemStudio.sln` 和 `backend` 的專案根資料夾。
2. 安裝 **C# Dev Kit**（含 .NET debugger）和 **Python** 擴充；先依上方方式建立 `.venv`。
3. 按 `Ctrl+Shift+P` → **Python: Select Interpreter**，選專案的 `.venv\Scripts\python.exe`。這設定 Python 編輯器／除錯器的 interpreter，不會自動傳給 WPF App。
4. 在 Run and Debug 建立或編輯 `.vscode\launch.json`，下列範例可分別用 F5 除錯 WPF App 或 Python worker。使用 App 設定時，`STEMSTUDIO_PYTHON` 會讓 Debug App 把 `.venv` Python 傳給子程序。

```json
{
  "version": "0.2.0",
  "configurations": [
    {
      "name": "StemStudio WPF",
      "type": "coreclr",
      "request": "launch",
      "preLaunchTask": "build-stemstudio-app",
      "program": "${workspaceFolder}\\src\\StemStudio.App\\bin\\Debug\\net8.0-windows\\StemStudio.App.exe",
      "args": [],
      "cwd": "${workspaceFolder}\\src\\StemStudio.App\\bin\\Debug\\net8.0-windows",
      "console": "internalConsole",
      "stopAtEntry": false,
      "env": {
        "STEMSTUDIO_PYTHON": "${workspaceFolder}\\.venv\\Scripts\\python.exe"
      }
    },
    {
      "name": "Python: model-status",
      "type": "debugpy",
      "request": "launch",
      "program": "${workspaceFolder}\\backend\\demucs_worker.py",
      "args": [
        "model-status",
        "--model-cache",
        "${env:LOCALAPPDATA}\\StemStudio\\models"
      ],
      "python": "${workspaceFolder}\\.venv\\Scripts\\python.exe",
      "cwd": "${workspaceFolder}",
      "console": "integratedTerminal",
      "justMyCode": false
    },
    {
      "name": "Python: separate",
      "type": "debugpy",
      "request": "launch",
      "program": "${workspaceFolder}\\backend\\demucs_worker.py",
      "args": [
        "separate",
        "--model-cache",
        "${env:LOCALAPPDATA}\\StemStudio\\models",
        "--input",
        "${input:audioFile}",
        "--output",
        "${env:TEMP}\\StemStudio-debug",
        "--stems",
        "vocals,guitar"
      ],
      "python": "${workspaceFolder}\\.venv\\Scripts\\python.exe",
      "cwd": "${workspaceFolder}",
      "console": "integratedTerminal",
      "justMyCode": false
    }
  ],
  "inputs": [
    {
      "id": "audioFile",
      "type": "promptString",
      "description": "要分離的音訊完整路徑",
      "default": "C:\\audio\\sample.wav"
    }
  ]
}
```

上述 WPF 設定使用 `preLaunchTask`，在 `.vscode\tasks.json` 加入建置工作：

```json
{
  "version": "2.0.0",
  "tasks": [
    {
      "label": "build-stemstudio-app",
      "type": "process",
      "command": "dotnet",
      "args": [
        "build",
        "${workspaceFolder}\\src\\StemStudio.App\\StemStudio.App.csproj",
        "--configuration",
        "Debug"
      ],
      "problemMatcher": "$msCompile",
      "group": "build"
    }
  ]
}
```

在 WPF 或 Python 原始碼左側行號欄設定 breakpoint，從 Run and Debug 選擇對應組態後按 F5。Python `model-status` 會很快結束，適合測模型快取檢查；要除錯完整推論可選 `Python: separate`，輸入測試音訊路徑。Python 環境須已安裝本專案所需套件與 PyTorch；若遇到 DLL、CUDA 或記憶體問題，先用 CMD 的直接命令確認 traceback 和執行環境，再判斷是否為程式本身問題。

### 分層診斷順序

1. **UI / WPF**：確認視窗事件有觸發、檔案已加入、音軌至少勾選一項，並查看 `StatusText` 與 MessageBox。
2. **Infrastructure / 程序啟動**：確認 App output 目錄含目前建置的 `StemStudio.Worker.exe`（或開發用 Worker DLL）、`backend\demucs_worker.py` 和 `.venv` Python 路徑有效。
3. **Worker**：檢查命令列參數、模型快取資料夾與 Python 子程序結束碼。Worker 會把 Python stdout/stderr 加上 `STATUS|` 前綴輸出；Infrastructure 將非零結束碼與錯誤內容顯示給 App。
4. **Python / Demucs**：使用上方獨立命令重現；檢查 traceback、Python 套件匯入、模型檔與輸入音訊。若獨立命令可重現，先在 Python 層修正；若獨立命令正常但 App 失敗，再回頭檢查 App 傳入的 executable、參數、環境變數和路徑。

## 6. 分軌資料流

1. WPF 接收檔案拖放或多選；App 檢查檔案存在與副檔名、避免同一檔案重複加入。
2. 使用者勾選 vocals/guitar/bass/drums，App 將輸入路徑和音軌清單組成 Core `SeparationRequest`。
3. App 啟動 worker。正式安裝使用 `{app}\python\python.exe` 和 `{app}\backend\demucs_worker.py`；只有 Debug 開發版本能透過 `STEMSTUDIO_PYTHON` 指定本機 Python 環境。
4. Python worker 把模型快取環境變數指向使用者設定的位置，並呼叫 `python -m demucs.separate --name htdemucs_6s`。
5. Demucs 將完整來源先寫入 `%LOCALAPPDATA%\StemStudio\staging` 下的唯一暫存目錄。
6. Python worker 只把使用者勾選的 WAV 複製到輸出目錄；成功後清除暫存。Infrastructure 檢查要求的 WAV 都存在，建立 Core `SeparationResult`。

輸入格式：WAV、MP3、FLAC、M4A、OGG、WMA、AAC。所有輸出均為 WAV。Demucs 會計算模型的來源，勾選只控制哪些 WAV 會複製並保留，不會縮減模型推論所需計算量。

## 7. 首次啟動、模型快取與設定

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

安裝包建置、publish 選項、模型封裝待辦與使用者安裝流程，已移至 [安裝包架構](安裝包架構.md)。

## 8. 測試與限制

- Release 解決方案可建置；Python worker 有語法檢查。
- 已使用安裝包內的 private Python 和 worker 實際檢查/下載模型、執行 Demucs，並用 CC0 吉他曲目驗證四種 stem 輸出。
- 已用 Inno Setup 編譯，並在測試目錄驗證安裝、安裝後 worker 啟動及解除安裝。
- 已測的吉他曲目為吉他主奏，不代表完整樂團混音的串音品質；仍需評估含鼓、貝斯、人聲的混音素材。
- 正式散布前需完成 Demucs、PyTorch、Python、各相依套件及模型權重的 license/再散布條款審查。

## 9. 待辦：分軌後的多樂器抓譜

### 目標與預期資料流

「抓譜」在此指從音訊估計音符事件（樂器、音高、起始／結束時間，視模型能力包含力度），輸出可編輯 MIDI；五線譜排版與吉他 TAB 是後續呈現／指法推定工作，不能假設模型 MIDI 會自動形成正確譜面。預計先採用「分軌後，各樂器分別抓譜」作為主要流程：

```text
原始音訊
  └─ Demucs htdemucs_6s
       ├─ vocals.wav  →（目前不在第一階段目標）
       ├─ guitar.wav  → 吉他音符／TAB 轉錄器 → guitar.mid / guitar.tab
       ├─ bass.wav    → 貝斯音符轉錄器       → bass.mid
       ├─ drums.wav   → 鼓事件轉錄器         → drums.mid
       ├─ piano.wav   → 鋼琴音符轉錄器       → piano.mid
       └─ other.wav   → 保留原 stem，暫不轉譜
```

### 實作待辦

- [ ] **建立評估資料集**：準備可合法使用、具有對齊 MIDI／譜面標註的完整混音與獨立 stem，涵蓋不同曲風、音色、速度及混音品質；將樣本分成開發集和不參與調參的保留測試集。
- [ ] **先做對照實驗**：同一批歌曲比較 A) 原始混音直接輸入多樂器模型，B) Demucs 分軌後依樂器輸入專用模型。記錄音符 precision/recall/F1、onset/offset 誤差、樂器分類、人工修正時間、耗時及顯存；另作人工盲聽／樂手檢查，不能只引用模型論文的跨資料集分數。
- [ ] **確認模型與散布條款**：多樂器候選先評估 YourMT3+；另行評估吉他轉錄／TAB 模型、鋼琴與貝斯模型、鼓轉錄模型。逐一核對程式碼、checkpoint、訓練資料與輸出格式的授權、可重現性、Windows 相容性、GPU／CPU 需求及模型維護狀態。研究中的 SOTA 結果不等於已可直接商用封裝。
- [ ] **定義 Core 契約**：新增與分軌解耦的轉錄請求／結果模型（輸入 stem、目標樂器、輸出 MIDI／可選 TAB、進度／取消），避免把特定 Python 套件或模型名稱洩漏到 WPF UI。
- [ ] **新增轉錄執行元件**：沿用 App → Infrastructure → Worker → Python 的程序邊界；以明確命令參數啟動轉錄 worker，支援進度、錯誤、取消與輸出檔驗證。第一版先以離線單機流程為目標。
- [ ] **建立樂器轉錄 adapters**：共用音訊正規化、取樣率與暫存管理；各模型 adapter 負責獨立的前處理、推論、音符後處理及 MIDI 軌道輸出。鼓需明確定義模型鼓類別至 General MIDI percussion key 的 mapping。
- [ ] **處理吉他 TAB 的限制**：MIDI pitch 不包含弦、把位、品格或演奏技巧。TAB 輸出需額外的吉他指法解碼與人工編輯介面；若資料不足或模型只輸出音符，先交付 MIDI，不宣稱完整 TAB。
- [ ] **設計結果 UX**：先讓使用者選擇要分軌及要抓譜的樂器，顯示各樂器工作進度；保留原始 stem、MIDI 及中間診斷結果，允許單獨重跑某一樂器與開啟結果資料夾。
- [ ] **定義驗收門檻**：在保留測試集設定各樂器最低音符 F1／onset 容差、可接受耗時和記憶體／VRAM 上限，並要求人工檢查無明顯漏軌、重複音符或不合理八度錯誤後再納入正式 UI。
- [ ] **評估硬體與封裝**：先支援 CPU 正確性，再依目標 NVIDIA GPU 與 CUDA/PyTorch 相容矩陣加入 GPU 加速及 CPU fallback。封裝前完成所有模型授權、dependency lock、權重 hash、離線安裝、磁碟空間與乾淨機測試。

## 參考

- [Demucs](https://github.com/adefossez/demucs)：分軌模型與命令列介面。
- [HTDemucs-6s 權重倉庫](https://huggingface.co/adefossez/HTDemucs-6s)：模型檔案、revision 與 metadata；公開可下載不代表可重新散布。
- [YourMT3+ 論文](https://arxiv.org/abs/2407.04822) 與 [官方程式庫](https://github.com/mimbres/YourMT3)：多樂器音符轉錄候選。
- [High Resolution Guitar Transcription](https://arxiv.org/abs/2402.15258) 與 [SynthTab](https://arxiv.org/abs/2309.09085)：吉他音符及 TAB 研究。
- [ADT-STR 論文](https://arxiv.org/abs/2601.09520) 與 [官方程式庫](https://github.com/pier-maker92/ADT_STR)：鼓 MIDI 轉錄候選。
- [Spotify Basic Pitch](https://github.com/spotify/basic-pitch)：單一樂器為主的多音高 MIDI 轉錄工具。
