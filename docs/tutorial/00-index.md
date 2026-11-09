# PracticeMate 教學文件：導覽

PracticeMate 是給社區管樂團團員在家練習用的 Windows 桌面工具：調音器、節拍器、錄音，錄完自動分析每個音的音準（音分）、每一拍的搶拍／拖拍（毫秒）、速度是否漸快漸慢，並檢查響度（LUFS）與真峰值（dBTP）。

**技術棧**：C# 13／.NET 9、WPF + MVVM（CommunityToolkit.Mvvm）、NAudio.Wasapi（只用於裝置 I/O）、xUnit；所有訊號處理（FFT、濾波、音高、起音、響度）皆自行實作。

**文件對應版本**：`commit 87dc795`（2026-11-09）

> 這套文件是給「開發結束一段時間後回來看的自己」。基礎概念不解釋，篇幅花在「為什麼這樣做」與「可以帶走的技巧」。
> 標記說明：📄 依據＝有文件、commit 或對話紀錄可查；🔍 推斷＝從程式碼反推，請自行確認。

## 閱讀路線

| 路線 | 章節 | 時間 |
|---|---|---|
| 快速掌握 | 00 → [03 架構](03-architecture.md) → [07 程式導讀](07-code-walkthrough.md) | 約 30 分鐘 |
| 完整學習 | 依序 01 → 12 | 約 3 小時 |
| 想學即時音訊與執行緒 | [03](03-architecture.md)「即時資料流」→ [07](07-code-walkthrough.md)「錄音一次的生命週期」→ [11](11-tradeoffs-pitfalls.md) | 約 45 分鐘 |
| 想學 DSP 演算法 | [08 核心演算法](08-algorithms.md) | 約 1 小時 |
| 想學「怎麼測一個訊號處理系統」 | [09 測試](09-testing.md) → [11 踩坑](11-tradeoffs-pitfalls.md) | 約 40 分鐘 |

## 章節清單

| 章節 | 檔案 | 狀態 |
|---|---|---|
| 01 需求分析 | [01-requirements.md](01-requirements.md) | 已產生 |
| 02 系統分析 | [02-system-analysis.md](02-system-analysis.md) | 已產生 |
| 03 架構設計 | [03-architecture.md](03-architecture.md) | 已產生 |
| 04 技術選型 | [04-tech-stack.md](04-tech-stack.md) | 已產生 |
| 05 資料庫設計 | — | **略過**：本專案沒有資料庫。持久化只有 WAV 檔與一個 JSON 設定檔，併入 06 章「輸入／輸出介面」說明 |
| 06 輸入／輸出介面與資料流 | [06-api-dataflow.md](06-api-dataflow.md) | 已產生（桌面程式無 HTTP API，改寫檔案格式、裝置 I/O 與資料流） |
| 07 程式內容導讀 | [07-code-walkthrough.md](07-code-walkthrough.md) | 已產生 |
| 08 核心演算法 | [08-algorithms.md](08-algorithms.md) | 已產生 |
| 09 測試 | [09-testing.md](09-testing.md) | 已產生 |
| 10 部署與環境設定 | [10-deployment.md](10-deployment.md) | 已產生 |
| 11 設計取捨與踩坑紀錄 | [11-tradeoffs-pitfalls.md](11-tradeoffs-pitfalls.md) | 已產生 |
| 12 延伸練習與自我檢核 | [12-exercises.md](12-exercises.md) | 已產生 |
| 名詞對照表 | [glossary.md](glossary.md) | 已產生 |

## 其他既有文件

- `README.md`：功能、建置、演算法摘要、效能數據、已知限制（對外的正式說明）
- `docs/design/architecture.md`：開發前的系統分析與架構設計（含 Q1–Q9 規格解讀、風險 R1–R7、里程碑 M0–M12）
- `docs/test-report.md`：驗收測試報告（25 項驗收逐項結果、發現的問題 F1–F4）

## 修訂紀錄

| 日期 | 對應 commit | 更新章節 |
|---|---|---|
| 2026-11-09 | `87dc795` | 初版，全部章節 |
