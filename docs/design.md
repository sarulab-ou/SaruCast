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

取得、Annex-B分割、音声処理、送信、制御の読み取りはバックグラウンドで実行し、WPFのUIスレッドを占有しない。stdoutのAnnex-BをAUD境界でアクセスユニットへ分割する。パイプの読み取り単位や一瞬の無出力をフレーム終端とみなさない。分割には最大1フレームの待ちがある。未分割バッファ上限8MiB、送信待ち4フレーム。通常の配信内容をファイルへ保存しない。

音声ON時のみNAudio.Wasapiで既定の再生出力をloopback取得する。ステレオ48kHz・16bit PCMに変換し、外部opus.dllの探索を無効化したConcentus 2.2.2で20ms・128kbpsのOpusへ変換。マイクは開かない。120msの有限PCMバッファと無音補充を使い、100msを超えるスケジューリング遅れでは古いPCMを捨てて時刻を進める。タイマーのまとめ発火時は期限に達したフレームを追い付かせる。既定出力変更や取得停止はエラーとして共有を止める。

## 同期・再送と成功判定

映像90kHz、音声48kHzの時刻を同じ単調時計へ対応付け、250msごとにRTCP Sender Reportを送る。音声時刻はサンプル数を基準とする。映像時刻はパイプ受信時刻から分割の1フレーム分を引く近似で、キャプチャ元の高精度PTSを引き継いだ計測ではない。厳密な音ずれと表示遅延は別途実測が必要。UDPパケットは1200byte以下、8個ごとに短い送信間隔を置く。映像と音声のcompound RTCPは各subpacketのSSRCで分ける。NACKに応じて暗号化済みの同一パケットを再送し、キャッシュは各stream最大16MiB・2秒で制限する。1秒ごとのキーフレームで映像の復帰を支える。高度な帯域推定・再交渉による適応画質制御は未実装。

OFFER成功だけで「配信中」にしない。3フレーム以上を送信し、異なるフレームの受信完了checkpointが2回以上進んだことを確認する。8秒間受信通知またはcheckpointが進まない場合は停止へ移る。受信完了は表示・再生そのものの証明ではなく、実機の目視・聴取と分けて記録する。

Receiverへ提案する200msのtargetDelayや、受信確認までの時間は、画面表示の遅延値ではない。ユーザーは約1秒を許容すると指定し、15fps実画面について「表示され、遅れも許容範囲」と確認した。秒数の実測は別途必要。

## 配布・停止・ネットワーク

許可されたZIP版のみを作る。`SaruCast-win-x64.zip`を全体展開し、最上位の`SaruCast.exe`を起動する。SelfContained=true、win-x64、PublishSingleFile=false。trimming／NativeAOTなし。.NETランタイム、DLL、tools/FFmpeg、第三者通知、対応FFmpegソース・ビルド設定を同梱する。利用者の.NET/VC++/FFmpeg/Bonjour追加インストール、初回依存ダウンロードは不要。設定・ログはユーザー領域へ保存し、配布先へ書き込まない。

mDNSを各IPv4 NICで照会し、手動IPv4も許可する。Cast TCP接続が選んだローカルIPv4へUDPソケットもbindし、選択したテレビのIP・回答ポートへconnectする。TCP制御は通常8009、メディアは回答された動的UDPポートと応答。HTTP待受はない。Firewallを全面的に無効化しない。暗号鍵やOFFER全体をログへ書かない。

停止時はキャンセル、映像取得／音声取得／送信タスク終了、UDPとTLS、暗号オブジェクト、FFmpegを解放する。Windows Job Objectは親の異常終了時も子エンコーダーを終了する。後始末の例外があっても残りの資源を解放し、停止完了タスクは共有して終了連打に対応する。再Closeは元のWPF Closingイベントを抜けてから行う。再接続は利用者が明示的に開始し、共有を自動再開しない。

Cast機器の自己署名TLSを許容する。device-auth証明書チェーン検証は未実装で、接続先の暗号学的な真正性を保証しない。信頼できるLANで使う試用版とする。
