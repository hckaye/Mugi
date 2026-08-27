# Kestrel 手動ホスト検証レポート

検証日: 2026-08-27

環境:

- macOS 26.5、arm64
- .NET SDK 10.0.203
- Microsoft.AspNetCore.App 10.0.7
- RID `osx-arm64`

## 結果

| 項目 | 結果 |
|---|---|
| `KestrelServer` と socket transport の直接生成 | 成功 |
| `IHttpApplication<TContext>` と feature API だけで `GET /` に応答 | 成功 |
| ポート衝突時の `StartAsync` 失敗処理 | 成功 |
| SIGINT 後の graceful shutdown | 成功。ただし終了 API は `DisposeAsync` ではなく `Dispose()` |
| ポート 0 の割り当て結果を `IServerAddressesFeature` から取得 | 成功 |
| NativeAOT publish と単体バイナリ実行 | 成功。IL/AOT 警告 0 件 |
| 明示した X.509 証明書による `UseHttps` | DI なしでは失敗 |
| `Expect: 100-continue` | Kestrel が自動応答することを確認 |
| 未消費リクエストボディの drain | 同じ HTTP/1.1 接続の再利用に成功 |

平文 HTTP の手動ホストは Miya の設計どおり実装できます。設計計画には修正が 2 点必要です。`KestrelServer` の公開終了 API は `Dispose()` であり、`DisposeAsync` はありません。また、.NET 10.0.7 の `UseHttps(X509Certificate2)` は `KestrelServerOptions.ApplicationServices` を参照するため、厳密な DI コンテナなしの構成では使えません。

## プロジェクト設定

プロジェクトは Web SDK ではなく `Microsoft.NET.Sdk` を使うコンソールアプリです。追加の NuGet パッケージはありません。

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <IsAotCompatible>true</IsAotCompatible>
    <PublishAot>true</PublishAot>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
</Project>
```

`WebApplication`、Generic Host、`ServiceCollection`、`BuildServiceProvider` は使っていません。`KestrelServerOptions.ApplicationServices` も設定していません。

## Kestrel の生成

必要だった生成コードは次のとおりです。

```csharp
var loggerFactory = NullLoggerFactory.Instance;

var transportFactory = new SocketTransportFactory(
    Options.Create(new SocketTransportOptions()),
    loggerFactory);

var kestrelOptions = new KestrelServerOptions();
kestrelOptions.Listen(IPAddress.Loopback, port);

var server = new KestrelServer(
    Options.Create(kestrelOptions),
    transportFactory,
    loggerFactory);
```

正確な型とコンストラクタ引数は次の組み合わせです。

- `SocketTransportFactory(IOptions<SocketTransportOptions>, ILoggerFactory)`
- `KestrelServer(IOptions<KestrelServerOptions>, IConnectionListenerFactory, ILoggerFactory)`
- `SocketTransportFactory` は `IConnectionListenerFactory` として第 2 引数へ渡せる
- endpoint は `KestrelServerOptions.Listen(IPAddress, int, Action<ListenOptions>)` で追加する
- 起動は `KestrelServer.StartAsync<TContext>(IHttpApplication<TContext>, CancellationToken)`
- 停止は `KestrelServer.StopAsync(CancellationToken)`
- 解放は `KestrelServer.Dispose()`

設計計画にあった `StopAsync` 後の `DisposeAsync` はコンパイルできませんでした。

```text
error CS1061: 'KestrelServer' に 'DisposeAsync' の定義が含まれておらず、
型 'KestrelServer' の最初の引数を受け付けるアクセス可能な拡張メソッド
'DisposeAsync' が見つかりませんでした。
```

検証コードは、起動に成功した場合だけ `StopAsync` を呼び、その成否にかかわらず `finally` で `Dispose()` を呼びます。`StartAsync` が失敗した場合も `Dispose()` へ進みます。

## `IHttpApplication<TContext>` の実装

`FeatureApplication` が次の 3 メソッドを実装しています。

- `CreateContext(IFeatureCollection)`
- `ProcessRequestAsync(FeatureContext)`
- `DisposeContext(FeatureContext, Exception?)`

`CreateContext` では以下を `IFeatureCollection` から直接取得します。

- `IHttpRequestFeature`: method、path、request body
- `IHttpResponseFeature`: status code、headers、response started 状態
- `IHttpResponseBodyFeature`: response body stream

`DefaultHttpContext` は作りません。レスポンスは status、`Content-Type`、byte 数で計算した `Content-Length` を設定してから、`IHttpResponseBodyFeature.Stream.WriteAsync` で書き込みます。

検証用 endpoint は次の 3 つです。

| method と path | 動作 |
|---|---|
| `GET /` | `text/plain; charset=utf-8` の `Hello\n` |
| `POST /read-body` | body を最後まで読み、byte 数を返す |
| `POST /ignore-body` | body を読まずに `ignored\n` を返す |

## feature とレスポンスの注意点

feature は 1 リクエストの処理中だけ有効です。`FeatureContext.Dispose()` は 3 つの feature 参照を消し、その後のアクセスを `ObjectDisposedException` にします。Miya でも feature や body stream をリクエスト終了後に保持しない実装が必要です。

`IHttpResponseBodyFeature.Stream` は Kestrel が所有するため、アプリ側で dispose しません。最初の body 書き込みでレスポンスが開始されます。status と headers は最初の書き込みより前に確定する必要があります。Miya のバッファ済みレスポンスは、全 middleware が戻った後、status と headers を設定してから body を 1 回送る構造にするとこの制約を守れます。ストリーミングへ移行した後は header 変更を拒否する必要があります。

## 平文 HTTP とポート 0

Debug build:

```console
$ dotnet build KestrelManualHost.csproj -c Debug --nologo
ビルドに成功しました。
    0 個の警告
    0 エラー
```

ポート 0 で起動すると、`StartAsync` 完了後に `IServerAddressesFeature.Addresses` から実ポートを取得できました。

```console
$ dotnet bin/Debug/net10.0/KestrelManualHost.dll --port 0
LISTENING http://127.0.0.1:54305
```

`curl` の結果:

```console
$ curl --fail-with-body --silent --show-error --include http://127.0.0.1:54305/
HTTP/1.1 200 OK
Content-Length: 6
Content-Type: text/plain; charset=utf-8
Server: Kestrel

Hello
```

`IServerAddressesFeature` は `server.Features.Get<IServerAddressesFeature>()` で取得しました。取得時点は `StartAsync` 完了後です。port 0 を設定した `KestrelServerOptions` 自体からは割り当て結果を取得できません。

## 起動失敗と停止

1 つ目のプロセスを port 55432 で起動し、同じコマンドをもう一度実行しました。2 つ目の `StartAsync` は `IOException` で失敗し、終了コードは 1 でした。失敗した server にも `Dispose()` が実行されました。

```text
START_FAILED System.IO.IOException: Failed to bind to address http://127.0.0.1:55432: address already in use.
DISPOSED
EXIT_CODE 1
```

起動済みプロセスへ Ctrl+C で SIGINT を送りました。

```text
SHUTDOWN_REQUESTED SIGINT
STOPPING
STOPPED
DISPOSED
```

`StopAsync` には既定 30 秒の timeout token を渡しています。この確認では処理中の長時間リクエスト、timeout 到達、停止中の 2 回目の signal は試していません。

## `Expect: 100-continue` と未消費 body

`scripts/probe_http.py` は raw TCP socket で HTTP/1.1 を送ります。

```console
$ python3 scripts/probe_http.py 54305
EXPECT_CONTINUE PASS: 100 Continue followed by 200 with body byte count 5
UNCONSUMED_BODY_DRAIN PASS: second request succeeded on the same connection
```

`Expect: 100-continue` の確認では、header だけを送って `HTTP/1.1 100 Continue` を受信してから 5 byte の body を送りました。アプリの `/read-body` が最初に request body を読むまで、クライアントは body を送っていません。最終応答は status 200、body は `5\n` でした。100 応答をアプリから明示的に送る処理はありません。

未消費 body の確認では、`POST /ignore-body` が 5 byte の body を読まずに応答した後、同じ TCP connection で `GET /` を送りました。2 回目も status 200 と `Hello\n` を受信できました。この結果から、今回の条件では Kestrel が未消費 body を処理し、接続を再利用できることが分かります。

この drain 確認は `Content-Length: 5` で body 全体を先に送った場合だけです。大きな body、遅い送信、chunked body、途中切断、drain の timeout と上限は Miya の統合テストで別に確認する必要があります。

## HTTPS

自己署名証明書と PFX は worktree 内の `.artifacts/certs/` に生成しました。

```console
$ openssl req -x509 -newkey rsa:2048 -sha256 -nodes \
    -keyout .artifacts/certs/server.key \
    -out .artifacts/certs/server.crt \
    -days 1 -subj '/CN=localhost' \
    -addext 'subjectAltName=DNS:localhost,IP:127.0.0.1'
$ openssl pkcs12 -export \
    -out .artifacts/certs/server.pfx \
    -inkey .artifacts/certs/server.key \
    -in .artifacts/certs/server.crt \
    -passout pass:spike-password
```

証明書は `X509CertificateLoader.LoadPkcs12FromFile` で読み、次の overload に渡しました。

```csharp
listenOptions.UseHttps(certificate);
```

この呼び出しは `StartAsync` より前の endpoint 設定中に失敗しました。

```console
$ dotnet bin/Debug/net10.0/KestrelManualHost.dll \
    --port 0 \
    --cert .artifacts/certs/server.pfx \
    --cert-password spike-password
Unhandled exception. System.ArgumentNullException: Value cannot be null. (Parameter 'provider')
   at Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService[T](IServiceProvider provider)
   at Microsoft.AspNetCore.Hosting.ListenOptionsHttpsExtensions.UseHttps(ListenOptions listenOptions, HttpsConnectionAdapterOptions httpsOptions)
EXIT_CODE 134
```

`UseHttps(X509Certificate2)` は内部で `HttpsConnectionAdapterOptions` を作る overload に進み、`ApplicationServices` からサービスを取得します。`ApplicationServices` が null の手動構成では通りません。`UseHttps(HttpsConnectionAdapterOptions)` も実際に試しましたが、同じ `provider` エラーになりました。

厳密に DI コンテナを使わない条件を維持するなら、Miya v0 の Kestrel アダプターが直接保証できるのは平文 HTTP です。HTTPS は TLS を終端する reverse proxy の後ろで使うか、HTTPS 用だけ別のホスト構成を許可する必要があります。Kestrel の内部サービスを型名で模倣する実装は、バージョン更新で壊れやすいため採用しない方が安全です。

## NativeAOT

実行した publish コマンド:

```console
$ dotnet publish KestrelManualHost.csproj \
    -c Release \
    -r osx-arm64 \
    --self-contained true \
    -o .artifacts/publish \
    --nologo
  KestrelManualHost -> .../bin/Release/net10.0/osx-arm64/KestrelManualHost.dll
  Generating native code
  KestrelManualHost -> .../spike/kestrel/.artifacts/publish/
```

`TreatWarningsAsErrors`、`IsAotCompatible`、`PublishAot` を有効にした状態で publish は成功し、IL trimming 警告と AOT 警告はありませんでした。

生成物:

```text
KestrelManualHost: Mach-O 64-bit executable arm64
BINARY_BYTES 6183040
```

バイナリ本体は 6,183,040 bytes、約 5.90 MiB です。別ディレクトリへバイナリ 1 ファイルだけをコピーして起動し、`curl` で `Hello\n` を受信しました。

```console
$ find .artifacts/single -maxdepth 1 -type f -print
.artifacts/single/KestrelManualHost
$ ./.artifacts/single/KestrelManualHost --port 0
LISTENING http://127.0.0.1:55127
$ curl --fail-with-body --silent --show-error http://127.0.0.1:55127/
Hello
```

起動時間は `scripts/measure_startup.py` で測りました。計測区間はプロセス生成の直前から、port 0 のアドレスを読み、その URL への `GET /` が成功して body を読み終わるまでです。単なる process entry までの時間ではなく、HTTP を受け付けられるまでの時間です。

```console
$ python3 scripts/measure_startup.py --runs 10 \
    ./.artifacts/publish/KestrelManualHost --port 0
RUNS_MS 65.154 17.902 20.780 17.408 18.179 15.944 18.100 15.699 15.861 19.461
MEDIAN_MS 18.001
```

- 10 回の中央値: 18.001 ms
- 最小: 15.699 ms
- 最大: 65.154 ms
- 1 回目: 65.154 ms

同じ Mac 上で直列実行した参考値です。CPU 負荷や filesystem cache は固定していません。

## Miya 本実装への推奨事項

1. 平文 HTTP のアダプターは、この spike と同じ 2 つの公開コンストラクタを使って実装できます。HTTP の起動に DI コンテナは不要です。
2. Miya が独自に `DisposeAsync` を公開する場合、その中で `StopAsync` を await した後に `KestrelServer.Dispose()` を呼びます。`KestrelServer.DisposeAsync` は呼べません。
3. `StartAsync` 完了前、起動済み、停止中、停止済みを区別し、起動失敗時にも `Dispose()` を必ず呼びます。`StopAsync` は起動成功後だけ呼びます。
4. port 0 の実アドレスは `StartAsync` 完了後に `IServerAddressesFeature` から取得し、Miya の起動結果またはログで利用者へ返します。
5. `IFeatureCollection` と取得した feature はリクエスト外へ漏らしません。Context を pool する場合は feature、body、path、header への参照をすべて消してから戻します。
6. バッファ済みレスポンスは status と headers を確定してから body を書きます。ストリーミング開始後の header 変更は例外にします。
7. `Expect: 100-continue` と通常の未消費 body 処理は Kestrel に任せられます。ただし drain の上限、timeout、chunked body、切断は統合テストに残します。
8. 「DI なしで明示証明書の HTTPS を保証する」という設計は .NET 10.0.7 では成立しません。平文 HTTP と reverse proxy を v0 の保証範囲にするか、HTTPS のときだけ別のホスト構成を許可するかを決める必要があります。
9. `NullLoggerFactory` でも bind 例外は呼び出し元へ返ります。Miya の `Run()` は例外の型と message を stderr へ出し、失敗を無音にしない実装にします。

## 検証用ファイル

- `KestrelManualHost.csproj`: net10.0、FrameworkReference、NativeAOT 設定
- `Program.cs`: Kestrel の生成、lifecycle、`IHttpApplication<TContext>`
- `scripts/probe_http.py`: 100 Continue と drain の wire 確認
- `scripts/measure_startup.py`: NativeAOT バイナリの起動時間計測

`bin/`、`obj/`、`.artifacts/` は `.gitignore` の対象です。
