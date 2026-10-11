# FileTree 磁碟空間分析：設計說明

版本 0.2.0，2026-10-10（0.2.0 新增取消掃描、單一資料夾掃描）。

## 目標

在 Windows 上快速找出最佔空間的資料夾與檔案。以系統管理員身分執行時，直接讀取 NTFS 的 MFT（主檔案表），不逐一列舉資料夾；沒有權限或磁碟不是 NTFS 時，改用一般的資料夾遍歷。

## 使用方式

| 方式 | 指令 |
| --- | --- |
| 一般啟動（會跳 UAC，拒絕則用慢速遍歷） | `FileTree.exe` |
| 啟動後自動掃描指定磁碟 | `FileTree.exe --scan C` |
| 啟動後自動掃描指定資料夾（請用絕對路徑） | `FileTree.exe --scan "C:\Program Files"` |
| 不要求提權 | `FileTree.exe --no-elevate` |
| 無介面基準測試，輸出 UTF-8 報告 | `FileTree.exe --bench C\|資料夾 [--fallback] [--out 報告.txt] [--tsv 資料夾.tsv] [--depth 3]` |

需求：Windows 10/11 x64、.NET 10 Desktop Runtime。

建置與測試：

```bash
dotnet test tests/FileTree.Tests/FileTree.Tests.csproj
```

```bash
dotnet publish src/FileTree/FileTree.csproj -c Release -r win-x64 --self-contained false -o publish
```

## 架構

```
Scanning/                       Model/                    UI/
  ScanService  選擇路徑            FileTreeModel  建樹        MainForm       主視窗、狀態列
  MftScanner   MFT 直讀    ──►    SizeFormat     容量字串 ──► TreeListView   可展開資料夾樹＋比例條
  RecordParser 紀錄解析（純函式）                              FileListView   最大檔案清單
  FallbackScanner 一般遍歷                                     TreemapControl 矩形樹狀圖（背景繪製）
  ScanData     原始陣列                                        Squarify       版面演算法
```

掃描器只產生扁平陣列（`ScanData`：parent、size、alloc、name、flags），由 `FileTreeModel.Build` 統一驗證與彙總，兩種掃描器共用同一套建樹與 UI。

## MFT 直讀流程（MftScanner）

1. 以 `FILE_FLAG_NO_BUFFERING` 開啟 `\\.\C:`（需要系統管理員）。
2. `FSCTL_GET_NTFS_VOLUME_DATA` 取得叢集大小、紀錄大小、MFT 起點與有效長度、空閒叢集數。
3. 讀取第 0 筆紀錄（$MFT 本身），解析 $DATA 的資料區段（mapping pairs），得到 MFT 在磁碟上的分段位置。
4. `PlanChunks` 把分段切成 4 MB、對齊磁區的讀取塊；稀疏分段略過但保留紀錄編號。
5. 平行讀取（最多 8 個執行緒），每筆紀錄交給 `RecordParser.TryParse`：
   - 套用 update sequence fixup，偵測寫到一半的紀錄。
   - $FILE_NAME：跳過 DOS 8.3 別名，取 Win32／POSIX 名稱與上層紀錄編號。
   - $DATA：常駐值取長度；非常駐只在 VCN 0 的片段取邏輯大小。
   - 佔用空間：加總每個非常駐屬性片段中「非稀疏分段」的叢集數。不信任屬性標頭的配置大小，因為 $BadClus:$Bad 標頭宣稱整顆磁碟，但分段全是稀疏。資料區段格式錯誤時才退回標頭值。
6. 每筆紀錄只寫自己的陣列格，平行階段不需上鎖；延伸紀錄先排隊，平行階段結束後再合併回基底紀錄。
7. 用 `(總叢集 − 空閒叢集) × 叢集大小` 作為 NTFS 已用空間，供驗算。

## 單一資料夾掃描（ScanService）

- 路徑長度大於磁碟根目錄就視為資料夾模式。
- 一般權限：`FallbackScanner` 直接從該資料夾開始遍歷。
- 系統管理員且為 NTFS：
  1. `MftScanner.LocateDirectory` 以 `FILE_READ_ATTRIBUTES`＋`FILE_FLAG_BACKUP_SEMANTICS` 開啟資料夾（不需系統管理員）。
  2. `GetFileInformationByHandle` 取得檔案 ID。NTFS 的檔案 ID 就是 MFT 檔案參照，低 48 位元是紀錄編號，高 16 位元是序號。
  3. `GetFinalPathNameByHandle` 解析連接點與掛載點，取得實際所在的磁碟代號與資料夾名稱。
  4. 讀取該磁碟整個 MFT，把 `ScanData.Root` 換成該資料夾的紀錄編號。資料夾外的紀錄走不到新的根，`Build` 會把它們當孤立紀錄排除。
  5. 驗證該紀錄是資料夾且名稱相符（不分大小寫）；不符就丟例外，改用一般遍歷。
- MFT 沒有依資料夾的索引，所以資料夾模式的耗時與整顆磁碟相同（約 1 秒），優勢在看得到無權限資料夾，不在速度。

## 取消掃描

- `MainForm` 每次掃描建立 `CancellationTokenSource`，「取消」按鈕呼叫 `Cancel()`。
- 取消權杖傳到三處：`MftScanner` 的 `Parallel.ForEach`（`ParallelOptions.CancellationToken`）、`FallbackScanner` 每進一個資料夾檢查一次並傳給 `Parallel.ForEach`、`ScanService` 建樹前再檢查一次。
- 取消時拋出 `OperationCanceledException`；`MainForm` 以 `catch (Exception) when (cts.IsCancellationRequested)` 接住，狀態列顯示「已取消掃描」，按鈕恢復。

## 建樹（FileTreeModel.Build）

- 可達性判斷：一筆紀錄的上層鏈必須經由「使用中的資料夾」走到根目錄。上層不存在、指向檔案、形成循環，都算孤立紀錄，排除在樹外，佔用空間計入 `UnattachedAlloc`。
- 子項目以 CSR（壓縮列）格式存放，依佔用空間由大到小排序。
- 廣度優先排序後反向累加，一次算完所有資料夾的大小與檔案數。
- `TopFiles`：最小堆積取前 N 大檔案。子項目已排序，堆積滿後遇到不比堆積最小值大的項目即可跳過該項目與後面的兄弟（資料夾的大小必定不小於其中任一檔案）。
- 建好後唯讀，UI 執行緒與背景繪製執行緒可同時讀取。

## 介面

- 工具列：磁碟選單、「掃描」（整顆磁碟）、「掃描資料夾…」（選擇資料夾對話框）、「取消」。掃描中工具列只有「取消」可按。
- 上半左：資料夾樹（虛擬清單、自繪），名稱欄自動填滿寬度，避免整列選取時水平捲動。欄位：名稱、佔用空間、大小、佔上層比例（比例條）、檔案數。
- 上半右：所選資料夾的前 1,000 大檔案，雙擊在樹中定位。
- 下半：矩形樹狀圖，背景執行緒繪製，新的繪製會取消舊的，視窗縮放有 120 ms 去抖動；點擊定位、滑鼠停留顯示路徑。
- 右鍵選單：在檔案總管中顯示、複製完整路徑。
- 狀態列：掃描模式、檔案與資料夾數、掃描量與磁碟已用（資料夾模式只顯示資料夾總量）、耗時、無權限資料夾數；右下角「製作者：黃政文」。

## 數字的意義

- 「佔用空間」是磁碟實際配置的位元組（叢集），「大小」是檔案邏輯長度。稀疏、壓縮檔案兩者差很多。
- MFT 模式下，根目錄佔用空間應等於 NTFS 已用空間；Windows 顯示的「已使用」在本機多約 5%，這段差額不在 NTFS 位元圖裡、不屬於任何檔案，推測是系統保留空間（Reserved Storage；`fsutil storagereserve query` 需系統管理員，尚未驗證）。
- 一般遍歷模式的佔用空間是以邏輯大小進位到叢集估算，詳見 [GAPS.md](../GAPS.md)。
