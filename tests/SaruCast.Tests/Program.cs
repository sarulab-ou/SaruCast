using System.IO;
using SaruCast;
using Encoder = SaruCast.Encoder;

string testData = Path.Combine(Path.GetTempPath(), "SaruCast-test-data-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("SARUCAST_DATA_ROOT", testData);
int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
void Reject(Action action, string name) { try { action(); } catch (InvalidDataException) { Check(true, name); return; } throw new Exception("FAIL: " + name); }
var frame = new CastFrame("source", "destination", CastClient.Receiver, "{\"type\":\"日本語\"}");
Check(CastWire.Decode(CastWire.Encode(frame).AsSpan(4)) == frame, "Cast protobuf UTF-8 round trip");
Reject(() => CastWire.Decode([18, 127, 1]), "Reject truncated protobuf");
Reject(() => CastWire.Decode([255, 255, 255, 255, 255, 255]), "Reject overlong protobuf varint");
try { await CastWire.ReadAsync(new MemoryStream(new byte[] { 0x7f, 0xff, 0xff, 0xff }), default); throw new Exception(); } catch (InvalidDataException) { Check(true, "Reject oversized Cast frame"); }
Check(Encoder.Size(1280, 720, 1080) == (1280, 720), "No upscaling");
Check(Encoder.Size(1920, 1280, 1080) == (1620, 1080), "Preserve aspect ratio");
Check(Encoder.Size(3840, 2160, 720) == (1280, 720), "720p downscale");
Check(new Settings().FrameRate == 15 && Settings.NormalizeFrameRate(0) == 15, "Default and invalid rate use 15fps");
Check(new[] { 5, 15, 30 }.All(n => Settings.NormalizeFrameRate(n) == n), "Supported frame rates");
Check(Mdns.Query(Mdns.Service)[5] == 1, "DNS query question count");
Reject(() => Mdns.Parse([0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xc0, 12, 0, 12, 0, 1]), "Reject DNS compression cycle");
var records = new[] { new DnsRecord(Mdns.Service, 12, [], "tv." + Mdns.Service), new DnsRecord("tv." + Mdns.Service, 33, [], "tv.local", 8009), new DnsRecord("tv.local", 1, [192,168,1,5]), new DnsRecord("tv." + Mdns.Service, 16, [6,102,110,61,84,86,49]) };
Check(Mdns.Resolve(records).Single() == new CastDevice("TV1", "192.168.1.5"), "DNS-SD friendly name and endpoint");
Check(Mdns.Resolve(records.Select(r => r with { Ttl = 0 })).Count == 0, "DNS goodbye ignored");
await NativeChecks.RunAsync(Check);
await ClosingChecks.RunAsync(false); Check(true, "WPF close without reentrant Close");
await ClosingChecks.RunAsync(true); Check(true, "WPF repeated close shares shutdown");
await ClosingChecks.RunConnectingAsync(); Check(true, "WPF close cancels pending native negotiation and sends STOP");
var neverStarted = new CastSession(); await Task.WhenAll(neverStarted.DisposeAsync().AsTask(), neverStarted.DisposeAsync().AsTask()); await neverStarted.DisposeAsync();
Check(true, "Session concurrent and repeated cleanup");
Directory.Delete(testData, true);
Console.WriteLine($"{passed} checks passed. Mock receiver is not a physical playback test.");
