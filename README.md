# FileTree 磁碟空間分析

Windows 磁碟空間分析工具，找出最佔空間的資料夾與檔案。以系統管理員身分執行時直接讀取 NTFS 的 MFT（主檔案表），不逐一列舉資料夾；沒有權限或非 NTFS 磁碟時改用一般資料夾遍歷。

本機實測（C: 磁碟，約 47 萬個檔案）：MFT 直讀 840 ms；一般遍歷冷快取 9.9 秒、熱快取 1.0 秒。數據與測試環境見 [docs/dev-log.md](docs/dev-log.md)。

## 功能

- 整顆磁碟或單一資料夾掃描，可中途取消
- 資料夾樹：佔用空間、邏輯大小、佔上層比例條、檔案數
- 所選資料夾的前 1,000 大檔案，雙擊在樹中定位
- 矩形樹狀圖（treemap），點擊定位、滑鼠停留顯示路徑
- 右鍵選單：在檔案總管中顯示、複製完整路徑
- 無介面基準測試模式（`--bench`），輸出 UTF-8 報告

## 需求

- Windows 10/11 x64
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

## 下載

到 [Releases](../../releases) 下載 zip，解壓後執行 `FileTree.exe`。

## 使用方式

| 方式 | 指令 |
| --- | --- |
| 一般啟動（會跳 UAC，拒絕則用一般遍歷） | `FileTree.exe` |
| 啟動後自動掃描指定磁碟 | `FileTree.exe --scan C` |
| 啟動後自動掃描指定資料夾（請用絕對路徑） | `FileTree.exe --scan "C:\Program Files"` |
| 不要求提權 | `FileTree.exe --no-elevate` |
| 無介面基準測試 | `FileTree.exe --bench C\|資料夾 [--fallback] [--out 報告.txt] [--tsv 資料夾.tsv] [--depth 3]` |

## 建置與測試

需要 .NET 10 SDK。

```bash
dotnet test tests/FileTree.Tests/FileTree.Tests.csproj
```

```bash
dotnet publish src/FileTree/FileTree.csproj -c Release -r win-x64 --self-contained false -o publish
```

架構與 MFT 直讀流程見 [docs/DESIGN.md](docs/DESIGN.md)。

## 已知限制

完整清單見 [GAPS.md](GAPS.md)，重點：

- 沒有刪除功能，找到大檔案後需自行到檔案總管處理
- 硬連結只計算一次，單一資料夾的數字可能偏小（總量正確）
- 一般遍歷模式的佔用空間是估算值
- $MFT 高度碎片化時無法直讀，自動改用一般遍歷
- 非 NTFS 磁碟（exFAT、FAT32、ReFS）未實機測試

## 授權

[MIT](LICENSE)
