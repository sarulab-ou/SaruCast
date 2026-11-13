# SaruCast 実装方針

2026-11-06。正式名称は **SaruCast**、起動ファイルは **SaruCast.exe**。0.1.2からCast Streamingを採用する。標準設定はユーザーの追加指示に合わせ、1080p上限・15fps・SDR。5／30fpsも選択可能。

## 採用経路と調査

テレビ内蔵のミラーリングReceiver `0F5096E8` を起動し、`urn:x-cast:com.google.cast.webrtc` のOFFER/ANSWERでH.264映像と、音声ON時のみOpusを交渉する。映像・音声をAES-128-CTRでフレーム単位に暗号化し、RTP/RTCPを同じUDPソケットで直接転送する。テレビへの追加アプリ、カスタムReceiver、独自アカウントは不要。一般的なWebRTC接続としては扱わない。

[Open Screenの仕様](https://chromium.googlesource.com/openscreen/+/8b62b21479f1adb7663f182d97962b960c5c64ef/cast/protocol/streaming_session_protocol.md)、[暗号化](https://chromium.googlesource.com/openscreen/+/8b62b21479f1adb7663f182d97962b960c5c64ef/cast/streaming/impl/frame_crypto.cc)、[パケット化](https://chromium.googlesource.com/openscreen/+/8b62b21479f1adb7663f182d97962b960c5c64ef/cast/streaming/impl/rtp_packetizer.cc)を確認した。libcastのstandalone実装はPOSIXを前提とする部分があり、そのままWindowsでビルドできるとは扱わず、ライブラリ自体は組み込んでいない。

[Classic Chromecast caster](https://github.com/RikvdReijen/classic-chromecast-caster/tree/6cd9fdd1c7b1ca7d88f1023eff4f13a3032c5bab) の実ソースとApache-2.0ライセンスを確認し、RTP/RTCP、交渉モデル、Annex-B分割、Concentusアダプターのみを固定commitから取り込んだ。元のWinUIアプリ・キャプチャ・インストーラーは使用しない。`src/SaruCast/ThirdParty/ClassicCast/README.md`に由来を記録。暗号nonceとパケット形式はOpen Screenに加え、独立実装CastMirror/chromiacastの実ソースとも照合した。参照commitはdependencies.jsonに記録する。READMEの主張だけで採用せず、本機のビルド、自動テスト、テレビの受信通知、利用者による表示・音声確認を行った。

従来のHLS/Default Media Receiver経路は製品から削除した。HTTPサーバーやセグメントファイルは作らず、HLSへ自動フォールバックしない。

## キャプチャ・エンコード

DXGI Desktop DuplicationをFFmpegのddagrabで使用する。DXGIのadapter/outputを列挙して選択した画面へ対応付ける。GDIは診断用の手動代替。カーソルあり。SDRのみ。WGC、HDRトーンマッピング、保護コンテンツの制限回避、個別ウィンドウ共有は対象外。

FFmpeg 9.0.2の公式ソースを固定してMinGWでビルドする。外部codecライブラリは不要。Windows標準Media Foundationのh264_mf（GPU／ソフトウェア）を使用し、baseline、Bフレームなし、1秒GOP、display_remoting、low_delayを設定する。Media Foundationが実際に出すlevelは機種によって異なり、交渉上限4.1以内を想定する。映像の目標ビットレートは15fpsで1080pが4Mbps、720pが2Mbps。5fpsはそれぞれ2／1Mbps、30fpsは6／3Mbps。

取得、Annex-B分割、音声処理、送信、制御の読み取りはバックグラウンドで実行し、WPFのUIスレッドを占有しない。stdoutのAnnex-BをAUD境界でアクセスユニットへ分割する。パイプの読み取り単位や一瞬の無出力をフレーム終端とみなさない。分割には最大1フレームの待ちがある。未分割バッファ上限8MiB、送信待ち4フレーム。0.1.3では満杯でもパイプ読み取りを待たせず、最も古いフレームを破棄する。連番の欠落や250ms超の待機を検出したら、参照関係が切れたPフレームを送り続けず、次のIDRを待つ。受信時刻を保持して、キューの待ち時間を新しいRTP時刻で隠さない。通常の配信内容をファイルへ保存しない。

## バッファの蓄積監視（0.1.3）

8パケットごとの1ms待機にはWindowsの高分解能waitable timerを使用する。`Task.Delay(1)`は環境によって約15.6msに伸び、30fpsで送信不足を起こし得る。ビジーループやシステム全体のタイマー分解能変更は行わない。映像・音声・再送は従来どおり同じUDP送信ロックを共有する。

FFmpegの`-progress pipe:2 -stats_period 0.25`の`out_time_us`と単調時計の経過差から、初回出力以後の処理遅れを監視する。キャプチャ元PTSの精密計測とは区別する。別の100ms周期タスクで、処理遅れ・未受信確認フレームの最古の送信時刻・新しいRTCPにあるplayoutDelayを確認する。UDP送信のロック待ちでも監視を止めない。8bit checkpointは最新送信番号を基準に展開し、初期255や逆行した確認では未確認フレームを消さない。保持は最大128件。5秒の起動猶予後、どれかが1000ms超で500ms続いた場合に再作成を要求する。

`StreamingSession`が接続単位の`StreamingConnection`を管理する。遅延による再作成時はFFmpeg、映像／音声処理、UDP、再送キャッシュ、TLS、Receiverのセッションを終了してから、同じ画面・音声・fps設定で新たなOFFERを行う。暗号鍵とSSRCも更新し、音声と映像の時計を一緒に初期化する。Receiverに届いたバッファを消すには送信側のキュー破棄だけでは不十分なため、全セッションを再作成する。再作成では一時的な映像・音声の中断を許容する。接続そのものの失敗は停止とし、自動再試行しない。1分内の自動再作成は最大3回。停止・終了・再作成は同じキャンセルと完了タスクで管理し、停止後に再接続しない。

クリアと再開確認をActivityイベントで通知し、診断ファイルとWPFの「配信ログ」に時刻付きで表示する。配信ログは詳細設定と独立して配信中にも開け、自動で展開する。画面内は最新100件に制限する。診断JSONには`bufferResets`、`droppedVideoFrames`、`activity`を追加し、セッション再作成前後の送信・受信カウンターを積算する。実際の表示・音ずれの観測は別途必要。

音声ON時のみNAudio.Wasapiで既定の再生出力をloopback取得する。ステレオ48kHz・16bit PCMに変換し、外部opus.dllの探索を無効化したConcentus 2.2.2で20ms・128kbpsのOpusへ変換。マイクは開かない。120msの有限PCMバッファと無音補充を使い、100msを超えるスケジューリング遅れでは古いPCMを捨てて時刻を進める。タイマーのまとめ発火時は期限に達したフレームを追い付かせる。既定出力変更や取得停止はエラーとして共有を止める。

## 同期・再送と成功判定

映像90kHz、音声48kHzの時刻を同じ単調時計へ対応付け、250msごとにRTCP Sender Reportを送る。音声時刻はサンプル数を基準とする。映像時刻はパイプ受信時刻から分割の1フレーム分を引く近似で、キャプチャ元の高精度PTSを引き継いだ計測ではない。厳密な音ずれと表示遅延は別途実測が必要。UDPパケットは1200byte以下、8個ごとに短い送信間隔を置く。映像と音声のcompound RTCPは各subpacketのSSRCで分ける。NACKに応じて暗号化済みの同一パケットを再送し、キャッシュは各stream最大16MiB・2秒で制限する。1秒ごとのキーフレームで映像の復帰を支える。高度な帯域推定・再交渉による適応画質制御は未実装。

OFFER成功だけで「配信中」にしない。3フレーム以上を送信し、異なるフレームの受信完了checkpointが2回以上進んだことを確認する。8秒間受信通知またはcheckpointが進まない場合は停止へ移る。受信完了は表示・再生そのものの証明ではなく、実機の目視・聴取と分けて記録する。

Receiverへ提案する200msのtargetDelayや、受信確認までの時間は、画面表示の遅延値ではない。ユーザーは約1秒を許容すると指定し、15fps実画面について「表示され、遅れも許容範囲」と確認した。秒数の実測は別途必要。

## 配布・停止・ネットワーク

ZIP版を主な配布形式とし、追加の依頼によりNSIS製の任意インストーラー版も用意する。`SaruCast-win-x64.zip`を全体展開し、最上位の`SaruCast.exe`を起動する。SelfContained=true、win-x64、PublishSingleFile=false。trimming／NativeAOTなし。.NETランタイム、DLL、tools/FFmpeg、第三者通知、対応FFmpegソース・ビルド設定を同梱する。利用者の.NET/VC++/FFmpeg/Bonjour追加インストール、初回依存ダウンロードは不要。設定・ログはユーザー領域へ保存し、配布先へ書き込まない。

インストーラーは既存のZIPをSHA-256で検証して利用し、アプリのEXE・DLL・FFmpegを変更しない。インストーラー用のREADME・文書・依存一覧・NSIS通知だけを追加更新し、専用のSHA256SUMS.txtを作る。原ZIPの一覧はZIP-SHA256SUMS.txtに保持する。現在のユーザーのLocalAppData/Programs/SaruCastへ配置し、HKCUのアンインストール登録とスタートメニュー／デスクトップのショートカットを作る。自動起動・自動配信・サービス・関連付け・Firewall変更は追加しない。更新時は空のフォルダーか所有マーカーのある既存インストールのみ許可し、使用中EXEへの上書きを拒否する。アンインストールは発行時の既知ファイルだけを削除し、ディレクトリを再帰削除しない。

mDNSを各IPv4 NICで照会し、手動IPv4も許可する。Cast TCP接続が選んだローカルIPv4へUDPソケットもbindし、選択したテレビのIP・回答ポートへconnectする。TCP制御は通常8009、メディアは回答された動的UDPポートと応答。HTTP待受はない。Firewallを全面的に無効化しない。暗号鍵やOFFER全体をログへ書かない。

停止時はキャンセル、映像取得／音声取得／送信タスク終了、UDPとTLS、暗号オブジェクト、FFmpegを解放する。Windows Job Objectは親の異常終了時も子エンコーダーを終了する。後始末の例外があっても残りの資源を解放し、停止完了タスクは共有して終了連打に対応する。再Closeは元のWPF Closingイベントを抜けてから行う。バッファ対策で配信中に行う再作成以外の再接続は、利用者が明示的に開始する。

Cast機器の自己署名TLSを許容する。device-auth証明書チェーン検証は未実装で、接続先の暗号学的な真正性を保証しない。信頼できるLANで使う試用版とする。

## アプリ内更新（0.1.4）

`AppUpdateService`がGitHub REST APIのリリース一覧（最大100件）を認証なしで取得する。`latest`だけでは既存の試用版を拾えないため、公開済み・非draft・数値タグの版を比較して最大の新しい版を選ぶ。対象リポジトリは`sarulab-ou/SaruCast`に固定し、通常ZIP／セットアップの正確なasset名と同じタグの`.sha256`を要求する。HTTP応答、metadata、ファイル長、ダウンロード量に上限を設け、GitHubが返すdigestとsidecar、実ファイルのSHA-256を照合する。ダウンロードは更新ボタンを押したときだけ行う。

ZIPはパスの逸脱、ドライブ名、予約名、case重複、symlink/reparseを拒否して新しい専用フォルダーへ展開する。全ファイルをSHA256SUMS.txtと照合する。アプリのバージョンもmetadataと照合する。展開上限2GiB、ファイル上限4096、圧縮ファイル上限512MiB。更新ファイルは`Paths.Root/updates/<GUID>`へ置く。

`UpdateManager`は起動中の版のEXE・DLL・同梱ランタイムを専用runnerへコピーし、同じSaruCast.exeの`--apply-update`を別プロセスで起動する。将来版の非公開CLIへ依存せず、追加ランタイムやPowerShellスクリプトも不要。ヘルパーは計画の所有フォルダー、起動場所、対象、旧版、PIDと開始時刻を照合し、元のアプリが終了するまで待つ。対象ごとに排他ロックを取る。60秒以内に終了しない元プロセスや、別の起動中インスタンスを強制終了しない。

ZIP版は旧manifestで所有が分かるファイルだけをバックアップして置換する。新規ファイルと利用者ファイルが衝突する場合は拒否する。不要になった旧所有ファイルだけを削除し、任意の追加ファイルを保つ。失敗した場合は変更した範囲を元に戻し、復元できない場合はバックアップを保持する。ディレクトリ全体を移動・再帰削除して更新する方式は使わない。

インストーラー版は所有マーカーとHKCUのInstallDirが一致した場合だけ識別する。新しいセットアップを検証し、元アプリ終了後に`/S /D=<同じ場所>`で起動して終了コードと新しい版を確認する。更新登録とUninstall.exeの整合を保つため、この版へZIPを直接上書きしない。NSIS更新が失敗した場合のファイル配置や登録はNSISに依存し、ZIP版と同等の自動ロールバックを保証しない。失敗時はログと手動更新を案内する。

設定・ログは従来のユーザーデータ領域を共有する。結果はupdates/last-result.jsonに原子的に保存し、再起動した対象アプリが読み取って配信ログへ表示する。成功した一時ファイルとバックアップはヘルパー終了後に片付け、失敗した更新の資料は保持する。アプリの起動自体では共有を再開しない。初回はこの機能を持つ0.1.4以降を手動導入する必要がある。

ユーザー書き込み可能なstagingを昇格したヘルパーに渡すことを避けるため、親アプリとヘルパーの双方で管理者トークンによる自動更新を拒否する。通常のasInvoker／利用者単位の導入を対象とし、自動昇格しない。
