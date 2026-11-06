# SaruCast 実装方針

2026-11-06。正式名称は **SaruCast**、起動ファイルは **SaruCast.exe**。

## 採用経路

公式の [libcast](https://chromium.googlesource.com/openscreen/+/refs/heads/main/cast/README.md)、[Streaming Session Protocol](https://chromium.googlesource.com/openscreen/+/refs/heads/main/cast/protocol/streaming_session_protocol.md)、[サンプル手順](https://chromium.googlesource.com/openscreen/+/HEAD/cast/docs/USING.md)、platform/BUILD.gn を確認した。調査時のrevisionを research/openscreen-revision.json に保存した。

単体サンプルはGN/depot_toolsと別途メディアライブラリを必要とする。platform/BUILD.gn のstandalone実装はPOSIXのTLS・UDP・ソケット待受・タスク実行とLinux/macOSのNIC列挙を使用する。Windows向けにはそれらの実装、Winsockとの接続、エンコーダーフレームの投入、認証・RTP/RTCP・暗号化・再送と同期の移植検証が必要。OFFER送信だけで映像転送が成立する構造ではない。今回libcastを採用・同梱していない。直接Cast Streamingは未対応。

初期版は [Default Media Receiver](https://developers.google.com/cast/docs/web_receiver)（CC1AD845）へH.264/AACのライブHLSを渡す**互換モード**。テレビ側の追加アプリ・独自Receiver・独自アカウント登録は不要。標準Receiverの起動に必要な通信までオフライン動作を保証しない。Googleの[対応メディア](https://developers.google.com/cast/docs/media)とCast制御の参照実装PyChromecastを確認。制御はTLS上のlength-prefixed protobuf＋JSON、CONNECT/heartbeat/LAUNCH/LOAD/MEDIA_STATUS/STOPを独立実装する。

## キャプチャ・エンコード

WGCはWPFからD3D/WinRTのフレーム管理を別途実装する必要があるため、最初の版は許容された代替DXGI Desktop DuplicationをFFmpegのddagrabで使用する。DXGIでadapter/outputを列挙し正しい画面を指定。GDIは診断用の手動代替。カーソルあり。SDRのみ。HDRのトーンマッピング、保護コンテンツ、個別ウィンドウ共有は未対応。

FFmpeg 9.0.2の公式ソースを固定し、MinGWで必要最小構成をビルド。外部codecライブラリは使わず、Windows標準Media Foundationのh264_mf（GPUまたはソフトウェア）とFFmpegのAACを利用。NAudio.Wasapi 2.2.1で既定出力のWASAPI loopbackを取得。一定速度PCM出力で無音区間を補い、既定出力変更は検出して停止・再開始を促す。HLSは1秒セグメント、12個のローリングリスト、キーフレーム1秒、削除猶予10個。6個の初期セグメントを準備してから標準ReceiverへLOADを送り、開始位置を明示してライブ端での再バッファを防ぐ。TV表示遅延はHLSバッファに依存し未実測。1秒未満を保証しない。

## 配布要件（追加指示を優先）

第一希望は単一EXEだが、プロセス分離したFFmpegとランタイムの配置・更新・ライセンス資料をそのまま保持するため、今回は許可された**ZIP版のみ**を完成させる。自己展開の完全性・排他・クリーンアップを追加実装する負担を避け、起動の安定性を優先する。両方式の実装は不要。

成果物は SaruCast-win-x64.zip。全体を展開し、最上位の SaruCast.exe を起動する。SelfContained=true、win-x64、WPF。PublishSingleFile=trueは指定しない。trimming/NativeAOTなし。DLLとランタイム、tools/ffmpeg.exe、ライセンス・対応FFmpegソースを同梱。利用者の.NET/VC++/FFmpeg/Bonjourの追加インストール、初回依存ダウンロードは不要。設定・ログ・HLS一時ファイルはユーザー領域。通常は非昇格。配布フォルダーへの書き込み不要。

## ネットワーク・停止

mDNS/DNS-SDを各IPv4 NICで照会、手動IPv4も許容。Cast接続で選ばれたローカルIPv4だけにHTTPサーバーをbind。HttpListenerのURL ACL/管理者権限を避けTcpListenerを使用。セッション毎の256bit乱数パス、TVの送信元IPだけ許可、固定ファイル名allowlist、ヘッダー長/時間/接続数制限。ログにURL秘密部分やTLSデータを書かない。停止でlistener/capture/audio/processを破棄し一時ファイル削除。自動再接続は0回とし画面共有を勝手に再開しない。ユーザーが再度開始する。

Cast機器の自己署名TLSを許容する。Castのdevice-auth証明書チェーン検証は未実装であり、LAN内の接続先の暗号学的な真正性は保証しない。信頼できるLANで使うテスト版とする。
