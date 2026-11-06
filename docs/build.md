# 再現手順

以下はリポジトリのソースツリーで実行する開発手順です。配布ZIPの通常利用には不要です。

Windows PowerShell、WSL Ubuntu、既存のmake/GCC/MinGW-w64クロスコンパイラが開発に必要。実行利用者には不要。SDKはシステムへインストールせず.toolsへ取得する。

```powershell
./scripts/bootstrap.ps1
# 初回、またはFFmpeg構成を変更したとき。内部でWSLを呼ぶ。
./scripts/publish.ps1 -RebuildFfmpeg
# 次回以降
./scripts/publish.ps1
# 起動中の配布フォルダーを保って、別の場所へ発行する場合
./scripts/publish.ps1 -PublishDirectory artifacts/release-v0.1.2
```

WSLでFFmpegだけをビルドするときも明示的に移動する。

```powershell
wsl bash -lc "cd '/mnt/c/Users/saru/Desktop/2026/SaruCast' && bash scripts/build-ffmpeg.sh"
```

全依存はdependencies.json、NuGetは各packages.lock.jsonで固定。取り込んだClassicCastのソースはGitで管理し、固定commit・変更箇所・Apache-2.0通知を同梱する。NAudioとConcentusを含むNuGetパッケージはlocked-modeで復元する。FFmpegソースSHA256とSDK SHA512を検証し、ネットワークからの依存取得は**開発ビルド時のみ**行う。

発行は.NET 10 LTS / win-x64 / SelfContained=true / PublishSingleFile=false。RuntimeFrameworkVersion=10.0.14を指定し、WPFのtrimming・NativeAOTを使用しない。外部PATHのFFmpegへ依存しない。FFmpegはLGPL構成、外部codecライブラリなし、MinGWランタイムは静的リンク。H.264はWindows標準Media Foundation、Opusは管理コードのConcentus。対応するFFmpegソース全体とビルド設定をZIPのsourcesへ含める。生成物はGit対象外。コンパイラ差によるバイナリの完全一致は保証しない。

成果物はartifacts/SaruCast-win-x64.zipと同名の.zip.sha256。ZIPの最上位にSaruCast.exeがあり、DLL、ランタイム、tools、licenses、sources、docsなどの発行出力をすべて維持する。受け入れは「ZIP全体を展開して最上位EXEを起動」で行う。EXEだけを抜き出すことをZIP版の条件にしない。自己展開と利用者側の初回依存ダウンロードは行わない。

## 自動検証

```powershell
$env:PYTHONUTF8='1'; $env:PYTHONIOENCODING='utf-8'; $env:CI='true'
./.tools/dotnet/dotnet.exe run --project tests/SaruCast.Tests/SaruCast.Tests.csproj -c Release
# 起動中の開発ビルドと出力を分けて検証する場合
./.tools/dotnet/dotnet.exe run --project tests/SaruCast.Tests/SaruCast.Tests.csproj -c Release '-p:OutputPath=C:\Users\saru\Desktop\2026\SaruCast\artifacts\test-build\'
# ランタイム、画面列挙、同梱FFmpegの存在だけ確認。取得・配信は始めない。
./artifacts/publish/SaruCast.exe --diagnose "$PWD/artifacts/startup.json"
```

自動テストのTLS/UDP模擬Receiverはテレビの表示・音声確認とは別。WPF終了試験は利用者のウィンドウを操作せず、表示しないテスト専用ウィンドウを使う。

## 実機へ明示的に配信する診断

以下は指定したテレビへ実際に送信する。`192.168.0.128`は検証例で、使用環境のテレビIPv4へ置き換える。

```powershell
# Cast Streamingのテスト模様＋テレビだけに確認音（PCでは発音しない）
./artifacts/publish/SaruCast.exe --streaming-test "$PWD/artifacts/pattern.json" --ip 192.168.0.128 --fps 15 --seconds 60 --pattern --audio
# 実画面とPCの既定出力音声。1080p上限。最大30分。
./artifacts/publish/SaruCast.exe --streaming-test "$PWD/artifacts/desktop.json" --ip 192.168.0.128 --fps 15 --seconds 1800 --1080p --audio
# 音声オフ、720p、ソフトウェアH.264
./artifacts/publish/SaruCast.exe --streaming-test "$PWD/artifacts/video-only.json" --ip 192.168.0.128 --fps 15 --seconds 20 --software
```

--fpsは5／15／30、標準15。--1080p省略時は720p上限。--audio省略時は映像だけで、音声デバイスを開かない。--softwareでGPUエンコードをオフ、--gdiで取得方式を変更。--patternは実画面取得の検証に含めない。診断のJSONは10秒ごとのCPU・メモリ・受信通知を記録し、正常終了時にsuccess=trueを追記する。televisionObservedPlaybackVerifiedは常にfalseで、物理的な表示・音声確認は利用者報告として検証記録へ別に残す。

## ZIP展開後の検証

空白入りの新しい検証フォルダーへ全展開し、同梱ハッシュ、外部.NETを参照しない起動、GPU／ソフトウェアH.264の4秒・60フレームの復号と、復号画像が全面黒ではないことを検査する。クリーンな別PCでの確認とは区別する。

```powershell
./scripts/verify-package.ps1 -DestinationDirectory 'artifacts/SaruCast 0.1.2 package check'
```

## 署名

使用できる証明書がある場合のみ：

```powershell
./scripts/publish.ps1 -CertificateThumbprint <CurrentUser/Myの証明書thumbprint>
```

最終EXEを署名し、タイムスタンプと署名状態を確認してからZIPを作る。省略時は未署名の試用版。今回署名は未実行。

## 任意のインストーラー版

ZIPが主な配布物。ショートカット登録を希望する利用者向けに`SaruCast-Setup-win-x64.exe`と同名の`.sha256`を追加する。既存ZIPを変更・再発行せず、そのアプリ一式を利用する。

```powershell
# ZIPと.zip.sha256を準備した後に実行する。任意でソースcommitを記録。
./scripts/build-installer.ps1 -PackagingRevision <installer-source-commit>
# 現在のユーザーのスタートメニューとデスクトップを実際に使う試験。
# 既存のSaruCast登録があれば中止し、試験後は試験用の登録を削除する。
./scripts/verify-installer.ps1
```

NSISはUbuntu公式の3.09-4ubuntu1の2パッケージをSHA-256固定で取得し、`.tools/nsis-root`へdpkg-debで展開する。システムへインストールしない。コンパイルはWSLで行う。開発時の取得以外にネットワークは不要で、利用者へNSISの導入を要求しない。

利用者単位の導入（RequestExecutionLevel=user）。両ショートカットを標準で作成し、アンインストール登録も現在のユーザーに限定する。任意の署名証明書は`-CertificateThumbprint`でセットアップEXEへ適用できる。今回の配布物は未署名。

既存v0.1.2 ZIPのハッシュとアプリは維持する。追加インストーラーのビルド定義は`v0.1.2-installer.1`タグで固定し、セットアップのInstaller-Info.txtへ入力ZIPのハッシュと包装用ソースcommitを記録する。元のv0.1.2タグは変更しない。
