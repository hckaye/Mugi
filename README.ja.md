# Miya

[English](README.md) | 日本語

Miya は高速でシンプルな .NET 向け Web アプリケーションフレームワークです。大きなフレームワーク群ではなく、無駄のないモダンな API を提供します。ハンドラーはラムダで書き、リクエストのルーティング、ミドルウェア、型付き入力のバインドと検証を行い、リクエストの読み取りとレスポンスの書き込みは 1 つのコンテキストオブジェクトで行います。`WebApplication`、Generic Host、DI コンテナなしで Kestrel の上で動きます。

Miya は NativeAOT のために作られています。実行時にリフレクション、アセンブリスキャン、実行時コード生成を一切使わないので、publish したアプリは数ミリ秒で起動し、小さな単一バイナリになります。ルーティング、JSON、型付き入力のバインダーはソースジェネレーターがコンパイル時に用意します。ジェネレーターを自分で呼ぶことはなく、パッケージを参照するだけで動きます。

## インストール

Miya のパッケージは `net9.0` を対象にし、.NET 9 以降で動きます。アプリのビルドには .NET 9 以降の SDK が必要です。ジェネレーターが、そのリリースで安定版になった C# のインターセプターを使うためです。

ランタイムパッケージとジェネレーターパッケージを追加します。型付き入力を使う場合は `Miya.Schema` も追加します。ジェネレーターはビルド中に動き、ルーティング、JSON、型付き入力のコードを生成します。

```xml
<PropertyGroup>
  <TargetFramework>net9.0</TargetFramework>
  <PublishAot>true</PublishAot>
  <InterceptorsNamespaces>$(InterceptorsNamespaces);Miya.Generated</InterceptorsNamespaces>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Miya" Version="0.1.0" />
  <PackageReference Include="Miya.Schema" Version="0.1.0" />
  <PackageReference Include="Miya.Generators" Version="0.1.0" />
</ItemGroup>
```

`InterceptorsNamespaces` の行は必須です。これによりジェネレーターは、認識できる呼び出しをより速い直接呼び出しに置き換えられます。`Miya.Generators` パッケージには、analyzer としてのジェネレーターと、この設定を自動で行う `buildTransitive` の props ファイルが入っています。パッケージが別のプロジェクト参照を経由して届く場合も同じです。

`Miya.Schema` は別パッケージです。型付き入力と検証を使うアプリで参照します。

リポジトリ内でプロジェクトを直接参照するときは、ジェネレーターを analyzer としてコンパイラに渡します。

```xml
<ItemGroup>
  <ProjectReference Include="../Miya/src/Miya/Miya.csproj" />
  <ProjectReference Include="../Miya/src/Miya.Schema/Miya.Schema.csproj" />
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

## 型付き入力と検証

`Miya.Schema` は、ルートパラメータ、クエリ、ヘッダー、JSON body のフィールドを 1 つの入力 record にまとめます。パースと検証が成功した場合だけハンドラーを呼びます。

```csharp
using Miya.Schema;

var searchSchema = Schemas.For<SearchInput>()
    .Query(input => input.Limit, rules => rules.Default(20).Range(1, 100));

app.Get("/search/:Page", searchSchema,
    static (c, input) => c.Json(input));

var personSchema = Schemas.For<CreatePersonInput>()
    .Body(input => input.Name, rules => rules.NotEmpty().MaxLength(80))
    .Body(input => input.Age, rules => rules.Range(0, 120))
    .Body(input => input.Note, rules => rules.Optional());

app.Post("/people", personSchema,
    static (c, input) => c.Json(input));

public sealed record SearchInput(int Page, string Query, int Limit);
public sealed record CreatePersonInput(string Name, int Age, string? Note);
```

明示した `Route`、`Query`、`Body`、`Header` の割り当てを優先します。明示していないフィールドは、名前が `:parameter` と完全に一致すればルートから取得します。それ以外は、`POST`、`PUT`、`PATCH` では JSON body から、他のメソッドではクエリから取得します。名前は ordinal かつ大文字と小文字を区別して比較します。`Header` には HTTP ヘッダー名も渡します。たとえば `.Header(input => input.RequestId, "X-Request-Id")` と書きます。

テキスト値はプリミティブ、`string`、`Guid`、Boolean、enum の名前または数値、`DateTime`、`DateTimeOffset` に対応します。body のフィールドは Miya の生成済み JSON codec で読みます。ジェネレーターはフィールドセレクターと検証規則をビルド時に読みます。実行時にセレクターを呼んだり、式木をコンパイルしたりはしません。

検証規則はチェーンできます。数値には `Min`、`Max`、`Range`、`Positive`、`NonNegative`、文字列には `NotEmpty`、`Length`、`MinLength`、`MaxLength`、`Pattern` を使えます。すべてのフィールドで `Optional`、`Default`、`Must` を使えます。

必須値の欠落、パース失敗、不正な JSON body、検証失敗ではハンドラーを呼ばず 400 を返します。`Content-Type` は `application/json` で、body は次の形です。

```json
{
  "errors": [
    { "field": "age", "message": "must be between 0 and 120" }
  ]
}
```

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

Miya は自前のシリアライザーで JSON を読み書きします。普通に使う分には設定は不要です。オブジェクトを返せば Miya が JSON として書きます。

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

ジェネレーターは、読み取れる呼び出し箇所から型を見つけます。型がジェネリックコードを通じてしかシリアライズされない場合、その型を名指しする呼び出し箇所がないので、ジェネレーターは見つけられません。そのような型は `Json.Include<T>()` で一度マークします。

```csharp
Json.Include<User>();
```

### codec を手書きする

codec は、1 つの型を JSON として読み書きする小さなクラスです。対応する型ごとに、ジェネレーターが codec を書きます。ジェネレーターが対応しない型を扱いたいとき、あるいは特定の JSON の形が必要なときは、`IJsonCodec<T>` を実装して codec を書き、`Json.Register` で登録します。登録した codec は、その型をシリアライズするすべての箇所で使われます。直接の `c.Json` 呼び出しも含みます。

```csharp
using Miya.Json;

Json.Register(UserCodec.Instance);

internal sealed record User(int Id, string Name);

internal sealed class UserCodec : IJsonCodec<User>
{
    public static UserCodec Instance { get; } = new();

    public void Write(ref JsonWriter writer, User? value)
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

    public User? Read(ref JsonReader reader)
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
                    ?? throw new JsonException("The name cannot be null.");
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

シリアライザーは、壊れた入力や悪意ある JSON がメモリやスタックを使い尽くせないよう上限を設けます。既定値はネットワークからの入力に対して安全で、`AppOptions` と `JsonOptions` で設定します。

| 設定 | 既定値 |
| --- | ---: |
| JSON リクエスト本文、`AppOptions.MaxJsonBodyBytes` | 1 MiB |
| JSON ドキュメント全体、`MaxDocumentByteLength` | 1 MiB |
| オブジェクトと配列の深さ、`MaxDepth` | 64 |
| 1 つの文字列トークン、`MaxStringByteLength` | 1 MiB |
| 1 つのオブジェクトのメンバー数または 1 つの配列の要素数、`MaxCollectionSize` | 1,048,576 |
| 1 つの数値の桁数、`MaxNumberDigits` | 128 |
| プールに保持する JSON 一時バッファ、`MaxPooledBufferByteLength` | 64 KiB |
| バッファリングするレスポンス、`AppOptions.MaxBufferedResponseBytes` | 1 MiB |
| リクエスト本文、`AppOptions.MaxRequestBodyBytes` | 30 MiB |

NaN と Infinity は既定で拒否します。`JsonOptions` は、時間のかかるシリアライズとパースのためのキャンセルトークンも持ちます。

ビルド時の最適化として、Miya は認識できる `c.Json` とルートの呼び出しを、生成コードへの直接呼び出しに置き換えます。これには interceptors という C# の機能を使います。この置き換えで観測できる挙動は変わりません。呼び出しが置き換えられたかどうかにかかわらず、シリアライズとルーティングの挙動は同じで、ジェネレーターが見つけられない呼び出しも、codec が登録されていれば動きます。

## コンパイラのジェネレーターなしでソースを生成する

ビルド構成によっては、コンパイラ統合のソースジェネレーターを動かせません。`miya-gen` は、同じ JSON とルーティングのコードを通常の `.cs` ファイルとして、ビルドの一手順で生成します。interceptors の最適化は出力しないので、直接呼び出しによる高速化だけがなくなります。挙動は同じです。

```sh
dotnet tool install --global Miya.Gen --version 0.1.0
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

## 事前生成なしの実行 (Miya.Reflection)

ルーティングとテキストレスポンスは、生成済みソースがなくても動きます。ソースジェネレーターと `miya-gen` のどちらも使えない環境で JSON を扱う場合は、opt-in の `Miya.Reflection` パッケージを追加します。このパッケージは public プロパティとコンストラクターから実行時に codec を作ります。

```xml
<PackageReference Include="Miya.Reflection" Version="0.1.0" />
```

起動時にフォールバックを有効にします。

```csharp
using Miya.Reflection;

ReflectionCodecs.Enable();
```

フォールバックは既定で無効です。生成 codec と同じプリミティブ、配列、`List<T>`、`Dictionary<string, T>`、nullable 値、enum、POCO、record を扱い、プロパティ名には camelCase を使います。`Miya.Reflection` は NativeAOT に対応しません。AOT で publish する場合は生成 codec を使います。
## OpenAPI 出力

`miya-gen openapi` はコンパイル済みプロジェクトのルートを読み、OpenAPI 3.1 ドキュメントを書き出します。

```sh
miya-gen openapi --project MyApp.csproj --output openapi.json
```

ルートパラメータは必須の path parameter になります。`Miya.Schema` を使うルートでは、path、query、header、JSON body の各フィールドについて、取得元、型、既定値、対応する検証条件も出力します。参照される JSON DTO は `components/schemas` に入ります。

レスポンスの判定はベストエフォートで、ルート登録時のハンドラーラムダを調べます。`c.Json(value)` があれば `application/json` のレスポンススキーマを、`c.Text(value)` があれば `text/plain` を出力します。どちらも判定できない場合、operation には content を指定しない 200 レスポンスだけが入ります。型付きルートには検証失敗時の 400 レスポンスも入ります。

## OpenAPI ドキュメントの取り込み

`Miya.Generators` はコンパイル時に既存の OpenAPI ドキュメントを読み込めます。上記の `miya-gen openapi` は C# のルートから OpenAPI ドキュメントを出力します。この設定は反対に、OpenAPI ドキュメントから C# を生成します。

JSON ファイルを `AdditionalFiles` に追加し、`MiyaOpenApi` を指定します。

```xml
<ItemGroup>
  <AdditionalFiles Include="api/openapi.json"
                   MiyaOpenApi="true"
                   MiyaOpenApiNamespace="MyApp.Api" />
</ItemGroup>
```

`MiyaOpenApiNamespace` は生成する型の名前空間を指定します。省略した場合はプロジェクトのルート名前空間を使います。

ジェネレーターは `components/schemas` から public な DTO record と string enum、各 operation のパスを持つ `Paths` クラス、operation ごとの入力 record と `ApiSchemas` フィールドを生成します。`/users/{id}` のような OpenAPI のパスパラメーターは、`/users/:id` のような Miya のパターンになります。生成される `.g.cs` はビルドごとに作り直されるため、変更は生成ソースではなく OpenAPI ドキュメントに加えます。

OpenAPI 3.0 と 3.1 の JSON に対応します。schema では object、string enum、string、Boolean、`int32`、`int64`、`float`、`double`、`decimal`、配列、ローカルの `components/schemas` 参照、nullable、required を扱えます。operation の入力には path、query、header parameter と JSON object の request body を使えます。数値の上下限、整数の排他的な上下限、文字列長、pattern、default、optional を `Miya.Schema` の rule に変換します。

合成 schema（`oneOf`、`anyOf`、`allOf`）、`additionalProperties`、外部参照、cookie parameter、JSON 以外の request body、`Miya.Schema` に対応する rule がない検証条件はスキップし、MIYA020 から MIYA023 の診断を出します。path と query parameter の名前は、生成される schema が同じ名前を使うため、有効な C# 識別子である必要があります。

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

`Run(int? port = null)` は loopback の HTTP/1.1 リスナーを起動し、キャンセルまたは終了シグナルまでブロックします。`Run()` はポートを未指定にするので `PORT` 環境変数が有効になります。`Run(8080)` はポートを明示的に選びます。`RunAsync(options, ct)` と `StartAsync(options, ct)` は非同期でホストします。`StartAsync` は、バインドしたアドレスと `StopAsync` を持つ `Server` を返します。ポート 0 は OS に空きポートを要求します。

ポートの選択は、まず明示的な `Run(port)` の値、次に `AppOptions.Port`、次に `PORT` に入った妥当な整数、最後に 3000 を使います。明示的またはオプションで渡された 0 から 65535 の範囲外の値は拒否します。`PORT` が不正な値のときは無視します。

SIGINT、SIGTERM、キャンセルは新規リクエストの受付を止め、処理中のものを待ちます。既定のシャットダウンタイムアウトは 30 秒です。2 回目のシグナルはプロセスを即座に終了します。

### HTTP/2 と HTTP/3

証明書がない場合、既定は HTTP/1.1 です。平文の HTTP/2 には `Protocols.Http2` を選びます。

```csharp
await app.RunAsync(new AppOptions
{
    Protocols = Protocols.Http2,
});
```

平文のリスナーは ALPN のネゴシエーションがないため、HTTP/1.1 と HTTP/2 を同時に提供できません。Miya はその組み合わせを起動時に拒否します。

`X509Certificate2` を渡すと、Miya 内で TLS を終端します。証明書を渡したときの既定は、接続ごとに ALPN で選ばれる HTTP/1.1 と HTTP/2 です。

```csharp
using System.Security.Cryptography.X509Certificates;

using var certificate = X509CertificateLoader.LoadPkcs12FromFile("server.pfx", "certificate-password");

await app.RunAsync(new AppOptions
{
    Certificate = certificate,
});
```

HTTP/3 は opt-in で、証明書が必要です。HTTP/1.1 と HTTP/2 を残したまま `Http3` フラグを追加すると、クライアントは Kestrel の `Alt-Svc` レスポンスヘッダーから HTTP/3 を発見できます。

```csharp
await app.RunAsync(new AppOptions
{
    Certificate = certificate,
    Protocols = Protocols.Http1AndHttp2AndHttp3,
});
```

HTTP/3 を要求しても `QuicListener.IsSupported` が false の場合、起動時に `PlatformNotSupportedException` を投げます。以下の計測に使った macOS arm64 環境では false を返したので、そこでは HTTP/3 の統合テストをスキップしました。

### Kestrel の高度な設定

`ConfigureKestrel` は、他の対応する Kestrel 設定に届きます。証明書の指定は `AppOptions.Certificate` に置きます。Miya は開発用証明書を探したり、Kestrel のエンドポイント設定ファイルを読んだりはしません。

`AppOptions.ConfigureServices` は、内部の Kestrel ホストに追加のサービスを登録します。Miya が依存性注入を必要とすることはありません。このフックは Kestrel を高度にカスタマイズするためだけのものです。設定すると、平文のエンドポイントでもサービス経由のホスティングパスを使います。登録したサービスはサーバー内部に留まり、ハンドラーやミドルウェアには届きません。

## パフォーマンス

Miya は高速で割り当ての少ない動作を目指しています。計測したシナリオでは次のとおりです。

- 生成された JSON シリアライズは、JIT と NativeAOT の両方で、平均時間と割り当てバイト数のどちらも System.Text.Json の source generation に並ぶか上回ります。
- ルーティングとミドルウェアパイプラインは、同期のホットパスで割り当てがありません（404 ミスと 405 不一致だけは、その小さなレスポンス状態を割り当てます）。
- `samples/Hello` の NativeAOT バイナリは約 6.8 MiB で、プロセス起動から数ミリ秒で最初のリクエストに応答します。

Miya は ASP.NET Core と同じ HTTP サーバーである Kestrel の上で動くので、素のリクエストスループットは、同じ処理をする ASP.NET Core アプリと同等です。サーバーが共通のボトルネックであり、Miya の違いはその上の薄い層にあります。これはスループットの高さではなく、リクエストあたりのメモリの少なさとして現れます。

数値、シナリオ、計測環境、再現方法は [docs/benchmarks.ja.md](docs/benchmarks.ja.md) にあります。


## v0 の制限

Miya v0 は、WebSocket のアップグレードと静的ファイルの配信に対応しません。認証、テンプレート、開発用証明書の探索、設定ファイル連携も提供しません。HTTP/3 は `QuicListener.IsSupported` と渡した証明書に依存します。TLS 終端のためのリバースプロキシは選択肢として使えます。

ルートのジェネレーターは、リテラルのパターンをコンパイル時に検証・解析し、解析済みのテンプレートを埋め込みます。照合はランタイムマッチャーが行います。ルート単位の照合コードや統合した trie はまだ出力しません。

診断 MIYA001 から MIYA004 は JSON とルートの生成を扱います。MIYA006 はリテラルの `c.Param` 呼び出しをハンドラーのルートと照合します。MIYA010 から MIYA015 は、型付き入力のルート割り当て、対応するフィールド型、スキーマ定義、検証規則、競合する割り当てを扱います。MIYA020 から MIYA023 は、不正な OpenAPI ドキュメント、未対応の schema 構造、Miya に変換できない値、生成名の衝突を扱います。プールされる派生コンテキストで消し忘れたフィールドを検出する MIYA005 は未実装なので、`IPoolableContext.OnReturn()` でそれらを消すのは呼び出し側の責任のままです。

## 謝辞

Miya の設計は、他のフレームワークやライブラリを参考にしています。

- [Hono](https://hono.dev) の API 設計を参考にしています。コンテキストオブジェクト（`c.Text`、`c.Json`、`c.Param`）、`:name` と `*name` のルート構文、onion 順のミドルウェア、Hono の `Hono<Env>` に対応する型付き `App<TContext>` です。
- [zod](https://zod.dev) は、型付き入力のコード定義バリデーションの参考です。
- JSON シリアライザーは [MessagePack-CSharp](https://github.com/MessagePack-CSharp/MessagePack-CSharp) と [MemoryPack](https://github.com/Cysharp/MemoryPack) の考え方に沿っています。`IBufferWriter<byte>` の上の `ref struct` ライター、ソース生成の codec、実行時ディスパッチではなく module initializer による登録です。
- Miya は ASP.NET Core の [Kestrel](https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel) の上で動きます。

## ライセンス

Miya は MIT License です。[LICENSE](LICENSE) を参照してください。

## サードパーティー表記

サードパーティーの謝辞は [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) に記録しています。
