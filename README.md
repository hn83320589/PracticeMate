# PracticeMate 管樂練習助手

[![CI](https://github.com/hn83320589/PracticeMate/actions/workflows/ci.yml/badge.svg)](https://github.com/hn83320589/PracticeMate/actions/workflows/ci.yml)

桌面練習工具：調音器、節拍器、錄音，並分析音準、節拍與響度。

> 開發中。設計文件：[docs/design/architecture.md](docs/design/architecture.md)

## 建置

需求：.NET SDK 9.0.x、Windows 10/11。

```powershell
dotnet build PracticeMate.sln -c Release
dotnet test PracticeMate.sln -c Release --filter "Category!=Performance"
```
