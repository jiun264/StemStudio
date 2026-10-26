# StemStudio

Windows 桌面 AI 樂曲分軌工具。拖放或選擇音訊檔，勾選要保留的人聲、吉他、貝斯、鼓音軌，並在本機完成處理。

## 模型決策

- **分軌模型：Demucs v4 `htdemucs_6s`**。
- 這個六來源模型同時輸出 vocals、drums、bass、guitar、piano、other；StemStudio 第一版提供人聲、吉他、貝斯、鼓四種選項。
- 吉他是實驗性來源，模型可能有音色外洩或殘留；品質取決於歌曲與混音。
- 音訊與模型推論留在本機。首次啟動會詢問是否下載約 55 MB 的模型權重；建議預留至少 1 GB 可用磁碟空間供模型快取及音訊輸出。
- 安裝包內含私有 CPython 3.12、CPU 版 PyTorch、Demucs 及獨立 worker；不依賴使用者全域 Python。模型權重不放入安裝包，首次使用才下載。

## 吉他分離初步驗證

- 使用 CC0 公共領域曲目 [Benny's Blues](https://archive.org/details/SlideGuitarExtravaganzaRedux)（David-Sky Marchant，來源 MP3 MD5：`29831b15d0f9f9cf553a86e684c78f59`）以安裝包內 `htdemucs_6s` 完整處理 134.55 秒音訊。
- 分軌成功輸出 vocals/guitar/bass/drums 四個 WAV。量測 guitar RMS 為 3564.5，其他單一音軌 RMS 約 201，吉他約為最響非吉他音軌的 17.7 倍（約 25 dB）。
- 這是以吉他為主的 solo 範例，足以確認模型有抓到吉他，但不能代表完整樂團混音的串音表現；仍需再用含人聲、鼓、貝斯與吉他的混音曲目評估。
- 替代模型考量：Demucs `htdemucs` 標準四來源只輸出 vocals/drums/bass/other，沒有獨立 guitar；因此不符合本專案目標。現階段保留 `htdemucs_6s`，待混音樣本測試後再比較具獨立吉他來源的模型。

## 架構

```text
StemStudio.sln
├── src/
│   ├── StemStudio.App/             WPF UI、拖放/檔案選取、工作流程
│   ├── StemStudio.Core/            分軌請求、結果、音軌型別與服務介面
│   ├── StemStudio.Infrastructure/  worker 整合、設定存取與輸出管理
│   └── StemStudio.Worker/          獨立 .NET worker 程序
├── backend/
│   ├── demucs_worker.py            Python/Demucs worker
│   └── requirements.txt            固定 CPU 推論套件版本
├── installer/                      Inno Setup 與建置腳本
└── docs/ARCHITECTURE.md             架構決策與安裝/擴充界線
```

更多模組責任、資料流與安裝策略請見 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

### 執行時技術架構

```text
StemStudio.App (WPF / .NET 8)
  └─ StemStudio.Infrastructure
       └─ 啟動 StemStudio.Worker.exe
            └─ 安裝目錄內的 python\python.exe
                 └─ backend\demucs_worker.py
                      └─ python -m demucs.separate
                           └─ Demucs htdemucs_6s
```

- **App** 負責拖放/選檔、音軌選擇、設定視窗、首次模型下載同意與磁碟空間提示；不直接載入 Python 或 PyTorch。
- **Core** 定義 `SeparationRequest`、`SeparationResult`、音軌型別及服務介面，讓 UI 不需要知道模型的輸出目錄或 CLI 實作。
- **Infrastructure** 以 `ProcessStartInfo.ArgumentList` 傳遞明確的 worker 命令與檔案路徑，從 stdout 的 `STATUS|` 訊息回報進度；stderr 與失敗輸出會保留作錯誤診斷。
- **.NET Worker** 是獨立程序邊界，驗證 Python、腳本和快取路徑，設定 `TORCH_HOME`、`HF_HOME`、`HF_HUB_CACHE` 後啟動私有 Python。正式安裝不讀取全域 Python/PATH。
- **Python worker** 提供 `model-status`、`prepare-model`、`separate` 三種命令。模型下載到使用者設定的 Hugging Face 快取位置；狀態檢查會相容 Windows 無法跟隨的快取 symlink。推論時先輸出到 LocalAppData 暫存區，成功後只把所選 WAV 複製到輸出資料夾。

按下取消時 App 會取消等待並終止 worker 的整個程序樹（包含 Demucs Python 子程序）。每個輸入檔案使用獨立、有唯一名稱的輸出資料夾；推論失敗會回報錯誤，不會當作成功。

## 開發環境

- Windows 10/11 x64
- .NET 8 SDK
- Python 3.12 x64（只供開發環境使用）

開發時可建立本機虛擬環境並指定給 App；正式安裝不會讀取這個環境：

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r .\backend\requirements.txt
$env:STEMSTUDIO_PYTHON = (Resolve-Path .\.venv\Scripts\python.exe).Path
dotnet run --project .\src\StemStudio.App\StemStudio.App.csproj
```

> 正式 App 透過獨立 `StemStudio.Worker.exe` 啟動安裝包附帶的 Python，不會尋找或呼叫全域 Python。開發執行若尚未建置私有執行環境，請設定 `STEMSTUDIO_PYTHON` 指向上述 `.venv` Python；worker 只在開發時使用這個明確指定的路徑。

輸入支援 WAV、MP3、FLAC、M4A、OGG、WMA、AAC。已選取的音軌會以 WAV 格式輸出至使用者的「音樂\StemStudio」資料夾；可一次加入多個檔案並依序處理。按「取消」會停止目前的模型工作。

## 建置安裝程式

安裝程式會把自包含 .NET App、獨立 worker、CPython embeddable runtime、CPU 版 PyTorch、Demucs 和 Python worker 一起封裝；模型權重在第一次啟動時下載。建置機需要 Windows x64、.NET 8 SDK、網路連線、足夠的暫存磁碟空間，以及 **Inno Setup 6**。

先在 PowerShell 切換到專案根目錄（包含 `StemStudio.sln` 的資料夾），再執行：

```powershell
Set-Location 'C:\path\to\StemStudio' # 替換成實際專案路徑
.\installer\build-installer.ps1
```

如果 Windows PowerShell 因執行原則而拒絕執行腳本，可只對這次建置使用 `Bypass`，不必永久更改目前使用者或整台電腦的執行原則：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\installer\build-installer.ps1
```

`-SkipRuntimeBuild` 可加在上述命令最後，用來重用已建置的私有 Python runtime，例如：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\installer\build-installer.ps1 -SkipRuntimeBuild
```

`Bypass` 會略過這個子程序的 PowerShell 執行原則檢查；只對自己信任的腳本使用。若電腦受到組織的群組原則管理，請遵循管理員設定，不要嘗試繞過受管理的限制。執行原則是 Windows PowerShell 的腳本執行控制，不是 StemStudio 專用設定；不同電腦的有效原則可能不同。一般不建議為了單次建置而永久設定 `Unrestricted` 或 `Bypass`。詳見 [Microsoft PowerShell 執行原則文件](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_execution_policies)。

腳本固定使用 CPython 3.12.10、Demucs 4.1.0 及 CPU 版 PyTorch 2.5.1，建置私有環境後產生 `artifacts\installer\StemStudio-Setup.exe`。依目前設定，安裝包約 220 MB；PyTorch 和 self-contained .NET 執行檔是主要體積來源，模型權重（約 55 MB）不在安裝包內。可用 `-SkipRuntimeBuild` 重用已建置的 Python runtime、`-SkipInstallerCompile` 只準備安裝 payload；只有在已安裝 Inno Setup 但未自動找到編譯器時，才需指定 `-InnoSetupCompiler "C:\...\ISCC.exe"`。

封裝腳本的技術步驟：

1. 下載 CPython embeddable x64 ZIP，解壓到 `artifacts\installer\payload\python`；修改 `python312._pth` 啟用 `site-packages`，再用 `get-pip.py` 安裝 pip。這是私有 runtime，不會安裝到系統或修改 PATH。
2. 使用 `backend\requirements.txt` 將 Demucs、CPU PyTorch、torchaudio 與 NumPy 安裝到該 runtime，並以私有 Python 匯入 Demucs、Torch 和檢查 Torch 版本。
3. `dotnet publish` 將 WPF App 及 `StemStudio.Worker` 分別輸出成 `win-x64` self-contained single-file EXE；Python worker 腳本也複製到 payload 的 `backend` 資料夾。
4. Inno Setup 讀取 `installer\StemStudio.iss`，將 App、worker、Python runtime 資料夾及 Python 腳本壓縮到單一安裝檔，建立開始功能表/可選桌面捷徑。

版本管理範圍：Python、Demucs、Torch、torchaudio、NumPy 直接依賴有固定版本；其餘 pip 傳遞相依目前由套件解析器在建置時選定，尚未以完整 lockfile 固定所有間接依賴。

### 使用者安裝流程

1. 執行 `StemStudio-Setup.exe`，選擇安裝位置；預設安裝到目前 Windows 使用者的 `%LOCALAPPDATA%\Programs\StemStudio`，不需系統管理員權限。
2. 安裝程式建立開始功能表捷徑，可選擇建立桌面捷徑；私有 Python/Demucs runtime 隨安裝包一併安裝。
3. 首次啟動時，App 先檢查模型狀態並顯示模型下載位置與磁碟可用空間；使用者同意後才下載模型權重到本機。
4. 在「設定」可變更輸出資料夾與模型快取資料夾；選擇音訊、音軌後開始分離。
5. 解除安裝只移除程式，不刪除使用者的模型快取、設定和輸出音訊。

安裝包採 CPU 推論以避免綁定特定 NVIDIA 驅動/CUDA。發佈前仍需確認 Demucs、PyTorch 及其相依套件的授權/再散布條款；目前沒有附帶模型權重。

## 目前進度

- [x] 建立獨立 Git 專案與 .NET 分層解決方案
- [x] 決定 Demucs v4 `htdemucs_6s` 作為第一版分軌模型
- [x] WPF 拖放/多檔案加入、音軌選取、狀態與取消流程
- [x] 以獨立 .NET worker 執行分軌；安裝版使用內附 Python，不依賴全域 Python
- [x] 首次啟動模型下載詢問、磁碟空間檢查與輸出/模型資料夾設定
- [x] Inno Setup 安裝規格與 runtime/worker/App 建置腳本
- [x] 以 CC0 真實吉他音訊驗證端到端分軌，並比較標準 Demucs 模型輸出能力
- [x] 建置 Windows 安裝檔並實測安裝、worker runtime 與解除安裝
- [ ] 以完整樂團混音進一步驗證吉他串音/品質

## 參考

- [Demucs](https://github.com/adefossez/demucs)：分軌模型與命令列介面。
