# Miya

[English](README.md) | 日本語

Miya は高速でシンプルな .NET 10 向け HTTP フレームワークです。大きなフレームワーク群ではなく、無駄のないモダンな API を提供します。ハンドラーはラムダで書き、リクエストの読み取りとレスポンスの書き込みは 1 つのコンテキストオブジェクトで行い、`WebApplication`、Generic Host、DI コンテナなしで Kestrel の上で動きます。

Miya は NativeAOT のために作られています。実行時にリフレクション、アセンブリスキャン、実行時コード生成を一切使わないので、publish したアプリは数ミリ秒で起動し、小さな単一バイナリになります。ルーティングと JSON はソースジェネレーターがコンパイル時に用意します。ジェネレーターを自分で呼ぶことはなく、パッケージを参照するだけで動きます。

Miya は `net10.0` を対象にします。以下の計測には .NET SDK 10.0.203 を使いました。

## インストール

ランタイムパッケージとジェネレーターパッケージを追加します。ジェネレーターはビルド中に動き、アプリのルーティングと JSON のコードを生成します。

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <PublishAot>true</PublishAot>
  <InterceptorsNamespaces>$(InterceptorsNamespaces);Miya.Generated</InterceptorsNamespaces>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Miya" Version="1.0.0" />
  <PackageReference Include="Miya.Generators" Version="1.0.0" />
</ItemGroup>
```

`InterceptorsNamespaces` の行は必須です。これによりジェネレーターは、認識できる呼び出しをより速い直接呼び出しに置き換えられます。`Miya.Generators` パッケージには、analyzer としてのジェネレーターと、この設定を自動で行う `buildTransitive` の props ファイルが入っています。パッケージが別のプロジェクト参照を経由して届く場合も同じです。

リポジトリ内でプロジェクトを直接参照するときは、ジェネレーターを analyzer としてコンパイラに渡します。

```xml
<ItemGroup>
  <ProjectReference Include="../Miya/src/Miya/Miya.csproj" />
  <ProjectReference Include="../Miya/src/Miya.Generators/Miya.Generators.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

## クイックスタート

```csharp
using Miya;

var app = new App();

app.Get("/", static c => c.Text("Hello"));
app.Get("/users/:id", static c => c.Json(new User(c.Param("id"), "Ada")));

app.Run();

public sealed record User(string Id, string Name);
```

起動してリクエストを送ります。

```sh
dotnet run
curl -i http://127.0.0.1:3000/users/42
```

`GET /users/42` は `{"id":"42","name":"Ada"}` を返します。既定のポートは 3000 です。`PORT=8080 dotnet run` はコードを変えずに待受ポートを変えます。

## コンテキストオブジェクト

すべてのハンドラーは 1 つのコンテキストを受け取ります。ここでは `c` と呼びます。届いたリクエストを読み、レスポンスを組み立てます。

リクエストを読む:

| 呼び出し | 返すもの |
| --- | --- |
| `c.Req.Method`、`c.Req.Path` | HTTP メソッドとパス |
| `c.Param("id")` | ルートパラメータ（`:id` のセグメントなど） |
| `c.Query("q")` | クエリ文字列の値、または null |
| `c.Req.Header("X-User")` | リクエストヘッダー、または null |
| `await c.Req.Text()` | リクエスト本文をテキストとして |
| `await c.Req.Json<T>()` | リクエスト本文を `T` にパースして |

レスポンスを書く:

| 呼び出し | 効果 |
| --- | --- |
| `c.Text(string)` | `text/plain` の本文を書く |
| `c.Json(value)` | `value` を JSON として書く |
| `c.Html(string)` | `text/html` の本文を書く |
| `c.Bytes(data, contentType)` | 生のバイト列を書く |
| `c.Stream(contentType, write)` | コールバックでレスポンスをストリーミングする |
| `c.Status(code)` | ステータスコードを設定する |
| `c.Header(name, value)` | レスポンスヘッダーを設定する。`c.AppendHeader` は値を追加する |
| `c.Redirect(location)` | リダイレクトを送る（既定は 302） |
| `c.NotFound()` | 404 を送る |

`c.Aborted` は、クライアントが切断したときに発火する `CancellationToken` です。レスポンスはハンドラーとミドルウェアが終わるまでバッファに保持されるので、本文を書いた後でもステータスやヘッダーを変えられます。ただしストリーミングが始まる前までです。

## ルーティング

HTTP メソッドとパスのパターンに対してハンドラーを登録します。パターンは、静的セグメント、1 セグメントの `:name`、残りのパスを受ける `*name`（末尾のみ）で構成します。

```csharp
app.Get("/users", ListUsers);
app.Get("/users/:id", GetUser);      // c.Param("id")
app.Get("/files/*path", GetFile);    // c.Param("path") が残りのパスを受ける
app.Post("/users", CreateUser);
```

`Get`、`Post`、`Put`、`Delete`、`Patch`、`Head`、`Options`、`All`、`On(method, ...)` がハンドラーを登録します。`app.Route(prefix, subApp)` は別の `App` をパスの prefix の下にマウントします。

2 つのパターンが同じパスに一致し得る場合は、より具体的な方が勝ちます。各セグメントで、静的テキストが `:name` より優先し、`:name` が `*name` より優先します。具体度が同じパターンは登録順に試します。

メソッドとパスの扱いは HTTP に従います。

- 既知のパスでメソッドが違う場合は、405 と `Allow` ヘッダーを返します。
- `GET` ルートは、同じパスの `HEAD` にも応答します。ヘッダーと `Content-Length` は同じで、本文はありません。
- 明示的な `OPTIONS` ルートがパスを処理しない場合、`OPTIONS` には `Allow` ヘッダー付きの 204 を返します。
- どのルートにも一致しないパスは 404 を返します。`app.NotFound(handler)` で独自のものを登録できます。

照合は Kestrel がすでにデコードしたパスを ordinal 比較で使います。エンコードされたスラッシュ（`%2F`）は照合中はエンコードのままなので、`/items/a%2Fb` は `/items/:id` に一致し、`c.Param("id")` がそれを `a/b` にデコードします。不正なパーセントエスケープは 400 を返します。`/users` と `/users/` は別のルートで、v0 では両者の間でリダイレクトしません。

## ミドルウェア

`app.Use` はすべてのリクエストを包みます。ミドルウェアはルートの前に登録順で走り、`next` が戻った後に逆順で戻ります。そのためリクエストと、処理を終えたレスポンスの両方に手を加えられます。`next` を複数回呼ぶと拒否されます。

```csharp
app.Use(static async (c, next) =>
{
    var stopwatch = Stopwatch.StartNew();
    await next(c);
    c.Header("Server-Timing", $"app;dur={stopwatch.Elapsed.TotalMilliseconds}");
});
```

`app.Use("/admin", middleware)` は、ミドルウェアを prefix 配下のパスに限定します。`app.OnError(handler)` は、リクエスト処理中に投げられた例外を扱います。

## JSON の返し方と読み方

Miya は自前のシリアライザー MiyaJson で JSON を読み書きします。普通に使う分には設定は不要です。オブジェクトを返せば Miya が JSON として書きます。

```csharp
app.Get("/users/:id", c => c.Json(new User(c.Param("id"), "Ada")));

app.Post("/users", async c =>
{
    var user = await c.Req.Json<User>();   // リクエスト本文をパース
    await c.Json(user);                    // JSON として書き返す
});

public sealed record User(string Id, string Name);
```

ビルド時にジェネレーターが各 `c.Json(...)` と `c.Req.Json<T>()` の呼び出しを読み、シリアライズする型を集め、それらを読み書きするコードを生成します。実行時には何も探索しません。これが NativeAOT で動く理由です。プロパティ名は既定で `camelCase` です。C# のプロパティ名の大文字小文字を保つには `<MiyaJsonNaming>PascalCase</MiyaJsonNaming>` を設定します。

### 対応する型

生成されるシリアライズは、真偽値と数値のプリミティブ、`char`、`string`、`Guid`、`DateTime`、`DateTimeOffset`、`decimal`、数値の enum、nullable な値、一次元配列、`List<T>`、`Dictionary<string, T>` に対応します。自分で定義した `public` または `internal` の class、record、struct は、これらの型を再帰的に組み合わせられます。record は primary constructor が必要です。通常のクラスはパラメーターなしコンストラクターと、アクセス可能な `get` と `set`/`init` を持つプロパティが必要です。

interface、`object`、ポリモーフィックな型、クラス継承、匿名型、private メンバー、ref-like 型、開いたジェネリック型、多次元配列、string 以外をキーに持つ dictionary は対象外です。使うと、その型を名指しするコンパイルエラーになります。

### ジェネリックコードを通じてしか現れない型

ジェネレーターは、読み取れる呼び出し箇所から型を見つけます。型がジェネリックコードを通じてしかシリアライズされない場合、その型を名指しする呼び出し箇所がないので、ジェネレーターは見つけられません。そのような型は `MiyaJson.Include<T>()` で一度マークします。

```csharp
MiyaJson.Include<User>();
```

### codec を手書きする

codec は、1 つの型を JSON として読み書きする小さなクラスです。対応する型ごとに、ジェネレーターが codec を書きます。ジェネレーターが対応しない型を扱いたいとき、あるいは特定の JSON の形が必要なときは、`IMiyaJsonCodec<T>` を実装して codec を書き、`MiyaJson.Register` で登録します。登録した codec は、その型をシリアライズするすべての箇所で使われます。直接の `c.Json` 呼び出しも含みます。

```csharp
using Miya.Json;

MiyaJson.Register(UserCodec.Instance);

internal sealed record User(int Id, string Name);

internal sealed class UserCodec : IMiyaJsonCodec<User>
{
    public static UserCodec Instance { get; } = new();

    public void Write(ref MiyaJsonWriter writer, User? value)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteRaw("{\"id\":"u8);
        writer.WriteNumber(value.Id);
        writer.WriteRaw(",\"name\":"u8);
        writer.WriteString(value.Name);
        writer.WriteRaw("}"u8);
    }

    public User? Read(ref MiyaJsonReader reader)
    {
        if (reader.TryReadNull())
        {
            return null;
        }

        var id = 0;
        var name = string.Empty;
        reader.ReadBeginObject();
        while (!reader.TryReadEndObject())
        {
            var property = reader.ReadPropertyName();
            if (property.SequenceEqual("id"u8))
            {
                id = reader.ReadInt32();
            }
            else if (property.SequenceEqual("name"u8))
            {
                name = reader.ReadString()
                    ?? throw new MiyaJsonException("The name cannot be null.");
            }
            else
            {
                reader.SkipValue();
            }
        }

        return new User(id, name);
    }
}
```

### 信頼できない入力に対する上限

MiyaJson は、壊れた入力や悪意ある JSON がメモリやスタックを使い尽くせないよう上限を設けます。既定値はネットワークからの入力に対して安全で、`MiyaOptions` と `MiyaJsonOptions` で設定します。

| 設定 | 既定値 |
| --- | ---: |
| JSON リクエスト本文、`MiyaOptions.MaxJsonBodyBytes` | 1 MiB |
| JSON ドキュメント全体、`MaxDocumentByteLength` | 1 MiB |
| オブジェクトと配列の深さ、`MaxDepth` | 64 |
| 1 つの文字列トークン、`MaxStringByteLength` | 1 MiB |
| 1 つのオブジェクトのメンバー数または 1 つの配列の要素数、`MaxCollectionSize` | 1,048,576 |
| 1 つの数値の桁数、`MaxNumberDigits` | 128 |
| プールに保持する MiyaJson 一時バッファ、`MaxPooledBufferByteLength` | 64 KiB |
| バッファリングするレスポンス、`MiyaOptions.MaxBufferedResponseBytes` | 1 MiB |
| リクエスト本文、`MiyaOptions.MaxRequestBodyBytes` | 30 MiB |

NaN と Infinity は既定で拒否します。`MiyaJsonOptions` は、時間のかかるシリアライズとパースのためのキャンセルトークンも持ちます。

ビルド時の最適化として、Miya は認識できる `c.Json` とルートの呼び出しを、生成コードへの直接呼び出しに置き換えます。これには interceptors という C# の機能を使います。この置き換えで観測できる挙動は変わりません。呼び出しが置き換えられたかどうかにかかわらず、シリアライズとルーティングの挙動は同じで、ジェネレーターが見つけられない呼び出しも、codec が登録されていれば動きます。

## コンパイラのジェネレーターなしでソースを生成する

ビルド構成によっては、コンパイラ統合のソースジェネレーターを動かせません。`miya-gen` は、同じ JSON とルーティングのコードを通常の `.cs` ファイルとして、ビルドの一手順で生成します。interceptors の最適化は出力しないので、直接呼び出しによる高速化だけがなくなります。挙動は同じです。

```sh
dotnet tool install --global Miya.Gen --version 1.0.0
dotnet build MyApp.csproj
miya-gen --project MyApp.csproj --output Generated
dotnet build MyApp.csproj
```

出力ディレクトリがプロジェクトのルート配下にあれば、SDK は `Generated/*.cs` を自動的にコンパイルします。外にあるディレクトリは `Compile` アイテムで追加します。生成の前にプロジェクトがコンパイルできる必要があり、出力ディレクトリにある既存の `Miya.*.g.cs` は置き換えられます。このリポジトリでは、同等のコマンドは次のとおりです。

```sh
dotnet run --project src/Miya.Gen -- \
  --project samples/Hello/Hello.csproj \
  --output samples/Hello/Generated
```

## 型付きコンテキスト

既定では、ハンドラーのコンテキストはリクエストとレスポンスのデータだけを運びます。自分の値をミドルウェアからハンドラーへ型安全に渡すには、`Context` を継承して `App<TContext>` を使います。文字列キーもキャストもありません。

```csharp
using Miya;

var app = new App<MyContext>();

app.Use(static async (c, next) =>
{
    c.CurrentUser = new User(c.Req.Header("X-User") ?? "anonymous");
    await next(c);
});

app.Get("/me", static c => c.Json(c.CurrentUser));

public sealed class MyContext : Context
{
    public User? CurrentUser { get; set; }
}

public sealed record User(string Id);
```

派生コンテキストはリクエストごとに新しく作られます。プールして再利用したい場合は、`IPoolableContext` を実装し、`OnReturn()` で自分のフィールドを消します。

## ホスティング

`Run(int? port = null)` は loopback の HTTP/1.1 リスナーを起動し、キャンセルまたは終了シグナルまでブロックします。`Run()` はポートを未指定にするので `PORT` 環境変数が有効になります。`Run(8080)` はポートを明示的に選びます。`RunAsync(options, ct)` と `StartAsync(options, ct)` は非同期でホストします。`StartAsync` は、バインドしたアドレスと `StopAsync` を持つ `MiyaServer` を返します。ポート 0 は OS に空きポートを要求します。

ポートの選択は、まず明示的な `Run(port)` の値、次に `MiyaOptions.Port`、次に `PORT` に入った妥当な整数、最後に 3000 を使います。明示的またはオプションで渡された 0 から 65535 の範囲外の値は拒否します。`PORT` が不正な値のときは無視します。

SIGINT、SIGTERM、キャンセルは新規リクエストの受付を止め、処理中のものを待ちます。既定のシャットダウンタイムアウトは 30 秒です。2 回目のシグナルはプロセスを即座に終了します。

### HTTP/2 と HTTP/3

証明書がない場合、既定は HTTP/1.1 です。平文の HTTP/2 には `MiyaProtocols.Http2` を選びます。

```csharp
await app.RunAsync(new MiyaOptions
{
    Protocols = MiyaProtocols.Http2,
});
```

平文のリスナーは ALPN のネゴシエーションがないため、HTTP/1.1 と HTTP/2 を同時に提供できません。Miya はその組み合わせを起動時に拒否します。

`X509Certificate2` を渡すと、Miya 内で TLS を終端します。証明書を渡したときの既定は、接続ごとに ALPN で選ばれる HTTP/1.1 と HTTP/2 です。

```csharp
using System.Security.Cryptography.X509Certificates;

using var certificate = X509CertificateLoader.LoadPkcs12FromFile("server.pfx", "certificate-password");

await app.RunAsync(new MiyaOptions
{
    Certificate = certificate,
});
```

HTTP/3 は opt-in で、証明書が必要です。HTTP/1.1 と HTTP/2 を残したまま `Http3` フラグを追加すると、クライアントは Kestrel の `Alt-Svc` レスポンスヘッダーから HTTP/3 を発見できます。

```csharp
await app.RunAsync(new MiyaOptions
{
    Certificate = certificate,
    Protocols = MiyaProtocols.Http1AndHttp2AndHttp3,
});
```

HTTP/3 を要求しても `QuicListener.IsSupported` が false の場合、起動時に `PlatformNotSupportedException` を投げます。以下の計測に使った macOS arm64 環境では false を返したので、そこでは HTTP/3 の統合テストをスキップしました。

### Kestrel の高度な設定

`ConfigureKestrel` は、他の対応する Kestrel 設定に届きます。証明書の指定は `MiyaOptions.Certificate` に置きます。Miya は開発用証明書を探したり、Kestrel のエンドポイント設定ファイルを読んだりはしません。

`MiyaOptions.ConfigureServices` は、内部の Kestrel ホストに追加のサービスを登録します。Miya が依存性注入を必要とすることはありません。このフックは Kestrel を高度にカスタマイズするためだけのものです。設定すると、平文のエンドポイントでもサービス経由のホスティングパスを使います。登録したサービスはサーバー内部に留まり、ハンドラーやミドルウェアには届きません。

## 計測結果

計測は 2026-08-27 に、Apple M5 CPU、物理 10 コア、macOS Tahoe 26.5.2、.NET SDK 10.0.203、.NET ランタイム 10.0.7 arm64 で行いました。BenchmarkDotNet 0.15.8 は、concurrent workstation GC、1 回の launch、5 回の warmup、10 回の計測を使いました。ホストはそれ以外の隔離をしていないので、近い結果を比較するときは BenchmarkDotNet のレポートにある誤差と標準偏差の列も見てください。

### NativeAOT サンプル

`dotnet publish samples/Hello/Hello.csproj -c Release` は IL や AOT の警告なしで完了しました。publish した実行ファイルは `dotnet` なしで動き、Text、JSON、404、405、HEAD のリクエストを通しました。

| 項目 | 結果 |
| --- | ---: |
| `Hello` 実行ファイルのサイズ | 7,128,392 bytes (6.80 MiB) |
| プロセス起動から `GET /` 完了まで、10 回の中央値 | 8.443 ms |

起動のサンプルは 21.598、9.278、8.435、8.452、8.848、8.710、7.641、8.389、7.446、8.114 ms でした。各回は新しい loopback ポートで新しい NativeAOT プロセスを起動し、HTTP レスポンスを受け取り終えてから停止しました。

### MiyaJson と System.Text.Json

シリアライザーの計測は 2026-08-28 に、上記の Apple M5、macOS arm64、.NET 10 環境で取り直しました。シリアライザーのジョブは、1 回の launch、5 回の warmup、20 回の計測、250 ms の iteration time を使いました。Miya は `Miya.Generators` が生成した codec を使い、codec を渡さない `MiyaJson.Serialize` と `MiyaJson.Deserialize` のオーバーロードで解決しました。ベンチマーク専用の codec は使っていません。

両方のシリアライザーは再利用した `IBufferWriter<byte>` に書き込みました。System.Text.Json は source generation、camelCase 命名、`UnsafeRelaxedJsonEscaping`、required メンバー検査、nullable 注釈検査を使いました。リクエスト JSON は計測区間の前に用意し、両シリアライザーが required プロパティの欠落と非 nullable プロパティへの null をどちらも拒否することを setup で確認しました。バッファ拡張のケースは各操作の中で 16 バイトのバッファを作りました。

合同のシリアライザー計測中、ホスト上で他の CPU 負荷の高いプロセスが動いていました。JIT の小さな DTO、DTO 100 件のリスト、リクエストバインドのケースは、それぞれのペアが近い条件で走るようカテゴリフィルターで測り直しました。JIT の表は、この 3 ケースにその個別計測を、残り 5 ケースに合同計測を使っています。NativeAOT の表は合同計測を使っています。

合格条件は、JIT と NativeAOT の両方で、すべてのシナリオにおいて Miya の平均時間と割り当てバイト数が System.Text.Json 以下であることです。これらの結果は JIT と NativeAOT の全 16 ケースで合格しました。割り当てバイト数はすべてのシナリオで System.Text.Json 以下でした。

JIT の結果:

| シナリオ | Miya 平均 | STJ 平均 | Miya 割り当て | STJ 割り当て |
| --- | ---: | ---: | ---: | ---: |
| 小さな DTO | 57.44 ns | 66.05 ns | 0 B | 0 B |
| DTO 100 件のリスト | 3,199.00 ns | 5,098.00 ns | 0 B | 0 B |
| ネストした DTO | 287.98 ns | 417.10 ns | 0 B | 0 B |
| エスケープの多い文字列 | 2,426.97 ns | 2,766.74 ns | 0 B | 0 B |
| 32 KiB の文字列 | 5,785.73 ns | 7,026.45 ns | 0 B | 0 B |
| 整数中心の DTO | 2,273.23 ns | 2,936.68 ns | 0 B | 0 B |
| リクエストバインド | 654.98 ns | 1,090.29 ns | 280 B | 872 B |
| バッファ拡張 | 5,780.44 ns | 16,059.42 ns | 32,880 B | 98,591 B |

NativeAOT の結果:

| シナリオ | Miya 平均 | STJ 平均 | Miya 割り当て | STJ 割り当て |
| --- | ---: | ---: | ---: | ---: |
| 小さな DTO | 45.97 ns | 58.77 ns | 0 B | 0 B |
| DTO 100 件のリスト | 7,577.34 ns | 9,414.40 ns | 0 B | 0 B |
| ネストした DTO | 219.01 ns | 420.45 ns | 0 B | 0 B |
| エスケープの多い文字列 | 2,372.24 ns | 2,772.62 ns | 0 B | 0 B |
| 32 KiB の文字列 | 3,984.00 ns | 5,925.40 ns | 0 B | 0 B |
| 整数中心の DTO | 2,745.76 ns | 4,106.62 ns | 0 B | 0 B |
| リクエストバインド | 635.99 ns | 980.47 ns | 280 B | 872 B |
| バッファ拡張 | 6,472.62 ns | 19,709.85 ns | 32,880 B | 98,602 B |

SpanJson 4.2.1 は JIT で別に計測しました。API が同じ `IBufferWriter<byte>` の契約に書き込むのではなく新しい `byte[]` を返すためです。合否の基準ではなく参考値です。

| シナリオ | SpanJson 平均 | 割り当て |
| --- | ---: | ---: |
| 小さな DTO | 50.71 ns | 64 B |
| DTO 100 件のリスト | 8,266.02 ns | 4,256 B |
| ネストした DTO | 228.06 ns | 168 B |
| エスケープの多い文字列 | 5,593.79 ns | 1,568 B |
| 32 KiB の文字列 | 39,181.72 ns | 32,800 B |
| 整数中心の DTO | 6,726.19 ns | 1,032 B |
| リクエストバインド | 194.46 ns | 280 B |

Miya の JIT 平均は、この 7 つの参考シナリオのうち 4 つで下回りました。SpanJson の平均は、小さな DTO、ネストした DTO、リクエストバインドのケースで下回りました。

### ルーティングとミドルウェアパイプライン

ルーティングのベンチマークは 10 個のルートを登録します。ハーネスは `Context` と最小限のインメモリ HTTP feature コレクションを再利用し、操作ごとにリセットして、`Build()` が返すハンドラーを呼びます。ソケットと Kestrel は含みません。

| ルートの結果 | JIT 平均 | JIT 割り当て | NativeAOT 平均 | NativeAOT 割り当て |
| --- | ---: | ---: | ---: | ---: |
| 静的ヒット | 261.8 ns | 0 B | 366.8 ns | 0 B |
| `:param` ヒット | 342.2 ns | 0 B | 374.8 ns | 0 B |
| ワイルドカードヒット | 258.9 ns | 0 B | 345.7 ns | 0 B |
| 404 ミス | 291.6 ns | 96 B | 394.6 ns | 96 B |
| 405 メソッド不一致 | 412.2 ns | 320 B | 639.8 ns | 320 B |

パイプラインのベンチマークは同じハーネスと静的なルートハンドラーを使います。

| ミドルウェア数 | JIT 平均 | JIT 割り当て | NativeAOT 平均 | NativeAOT 割り当て |
| ---: | ---: | ---: | ---: | ---: |
| 0 | 208.3 ns | 0 B | 259.8 ns | 0 B |
| 5 | 330.9 ns | 0 B | 426.8 ns | 0 B |

ベンチマークのコマンドは次のとおりです。

```sh
dotnet build benchmarks/Miya.Benchmarks/Miya.Benchmarks.csproj -c Release
MIYA_BENCHMARK_FINAL=1 dotnet run -c Release --no-build \
  --project benchmarks/Miya.Benchmarks -- --filter '*'
MIYA_BENCHMARK_FINAL=1 dotnet run -c Release --no-build \
  --project benchmarks/Miya.Benchmarks -- \
  --jit-only --filter '*SmallDto*' '*RequestBind*'
MIYA_BENCHMARK_FINAL=1 dotnet run -c Release --no-build \
  --project benchmarks/Miya.Benchmarks -- \
  --jit-only --filter '*List100*'
MIYA_BENCHMARK_FINAL=1 dotnet run -c Release --no-build \
  --project benchmarks/Miya.Benchmarks -- --routing
MIYA_BENCHMARK_FINAL=1 dotnet run -c Release --no-build \
  --project benchmarks/Miya.Benchmarks -- --spanjson
```

## v0 の制限

Miya v0 は、WebSocket のアップグレード、静的ファイルの配信、OpenAPI ドキュメントの生成に対応しません。認証、バリデーション、テンプレート、開発用証明書の探索、設定ファイル連携も提供しません。HTTP/3 は `QuicListener.IsSupported` と渡した証明書に依存します。TLS 終端のためのリバースプロキシは選択肢として使えます。

ルートのジェネレーターは、リテラルのパターンをコンパイル時に検証・解析し、解析済みのテンプレートを埋め込みます。照合はランタイムマッチャーが行います。ルート単位の照合コードや統合した trie はまだ出力しません。

診断 MIYA001 から MIYA004 は、匿名 JSON 型、不正なルート、限定的な重複ルート検出、非対応の JSON 型を扱います。プールされる派生コンテキストで消し忘れたフィールドを検出する MIYA005 は未実装なので、`IPoolableContext.OnReturn()` でそれらを消すのは呼び出し側の責任のままです。

## ライセンス

Miya は MIT License です。[LICENSE](LICENSE) を参照してください。

## サードパーティー表記

サードパーティーの謝辞は [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) に記録しています。
