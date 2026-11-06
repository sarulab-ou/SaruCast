# 再現手順

Windows PowerShell、WSL Ubuntu、既存のmake/GCC/MinGW-w64クロスコンパイラが開発に必要。
実行利用者には不要。SDKはシステムインストールせず.toolsへ取得する。

```powershell
./scripts/bootstrap.ps1
# 初回のみ、またはFFmpeg構成を変更したとき。内部でWSLを呼ぶ。
./scripts/publish.ps1 -RebuildFfmpeg
# 次回以降
./scripts/publish.ps1
# 起動中の配布フォルダーを維持して、別の場所へリリース用出力を作る場合
./scripts/publish.ps1 -PublishDirectory artifacts/release-v0.1.1
```

WSLだけでFFmpegをビルドするときは対象ディレクトリへ明示的に移動する。

```powershell
wsl bash -lc "cd '/mnt/c/Users/saru/Desktop/2026/SaruCast' && bash scripts/build-ffmpeg.sh"
```

全依存はdependencies.json、NuGetは各packages.lock.jsonで固定。
ソースのSHA256とSDKのSHA512を検証する。依存ネットワーク取得は**開発ビルド時だけ**。
発行は.NET 10 LTS / win-x64 / SelfContained=true / PublishSingleFile=false。
RuntimeFrameworkVersion=10.0.14を指定し、WPFのtrimming・NativeAOTを使用しない。
外部PATH上のFFmpegを参照しない。FFmpegはLGPL構成、外部codecライブラリなし、
MinGWランタイムは静的リンク。H.264はWindows標準Media Foundation、AACはFFmpeg内蔵。
完全な対応ソースとビルド設定をZIP内sourcesへ含める。大きな生成物はGit対象外。
初回のビルド環境はWSL Ubuntu / MinGW-w64 GCC 13-win32。コンパイラ差によりバイナリの完全一致は保証しない。

出力はartifacts/SaruCast-win-x64.zip。ZIPの最上位にSaruCast.exeを置き、発行出力を全て維持。
artifacts/publishは生成物として再生成する。受け入れは「ZIP全体を展開して最上位EXEを起動」
で行い、EXE単体での起動をZIP版の条件にしない。

開発時の単体検証：

```powershell
$env:PYTHONUTF8='1'; $env:PYTHONIOENCODING='utf-8'; $env:CI='true'
./.tools/dotnet/dotnet.exe run --project tests/SaruCast.Tests/SaruCast.Tests.csproj -c Release
# 実画面と音声の取得を明示的に開始し、終了時に一時メディアを消す。
./artifacts/publish/SaruCast.exe --diagnose "$PWD/artifacts/capture.json" --capture-seconds 10 --audio --software
```

--diagnoseの追加引数：--discover、--720p、--gdi、--pattern、--probe-ip <IPv4>。
--patternはテスト映像と440Hz音声を生成する。実画面・実音声の検証には含めない。
--capture-secondsと--cast-ip <IPv4>を併用すると、明示したテレビへ実際に配信する。
--pattern併用時の音はテレビだけへ送り、PCの音声取得・PCでの発音はしない。
--toneはPCでテスト音を鳴らす。--audioと併用してWASAPI取得を検証する用途に限る。
自動テストは模擬TLS Cast受信機を使うため、テレビ実機の成功とは別。

署名証明書がある場合だけ：

```powershell
./scripts/publish.ps1 -CertificateThumbprint <CurrentUser/Myの証明書thumbprint>
```

署名は最終SaruCast.exeへ実施し、タイムスタンプ・署名状態を確認した後にZIPを作る。
省略時は未署名のテスト版。署名付き配布の工程は今回未実行。
