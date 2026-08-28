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
| `c.Req.QueryAll("tag")` | クエリパラメータのデコード済みの全値 |
| `c.Req.Header("X-User")` | リクエストヘッダー、または null |
| `c.Req.Cookie("session")` | 指定名の最初のリクエスト cookie、または null |
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

### Cookie

`c.Req.Cookie(name)` は、指定名の最初の cookie を読みます。`c.SetCookie` は `Set-Cookie` レスポンスヘッダーを追加し、`c.DeleteCookie` は cookie を期限切れにします。既定の属性は `Path=/` と `SameSite=Lax` です。必要に応じて `CookieOptions` で `HttpOnly`、`Secure`、`Domain`、`MaxAge`、`Expires`、`SameSite` を設定します。`SameSite=None` には `Secure` が必要です。

署名付き cookie は HMAC-SHA256 を使い、`value.base64url(HMAC-SHA256(UTF-8(value)))` の形で書きます。`SetSignedCookie` とすべての `SignedCookie` の読み取りに、アプリケーションが同じ空でないキーを渡します。Miya はキーを保存したりローテーションしたりしません。署名がない場合や不正な場合は null を返します。

```csharp
app.Get("/session", c =>
{
    c.SetSignedCookie("account", "alice", "01234567890123456789012345678901"u8);
    var account = c.Req.SignedCookie(
        "account",
        "01234567890123456789012345678901"u8);
    return c.Text(account ?? "anonymous");
});
```

### 接続情報と複数のクエリ値

`c.Req.RemoteAddress`、`RemotePort`、`LocalAddress`、`LocalPort` は、トランスポートが提供する接続先情報を公開します。接続情報を持たないトランスポートでは、`RemoteAddress` と `LocalAddress` は null、ポートは 0 です。インプロセスのテストもこれに含まれます。`c.Req.Protocol` は `HTTP/1.1` や `HTTP/2` などを返し、`c.Req.IsHttps` はリクエストのスキームが HTTPS かどうかを返します。

`c.Req.QueryAll(name)` は、同じ名前の値をリクエスト順にすべて返します。`c.Query` と同じ規則でデコードし、`+` は空白になり、不正なエスケープは 400 になります。名前がなければ空の配列を返します。

### Form data

`await c.Req.Form()` は `application/x-www-form-urlencoded` と `multipart/form-data` の本文を `FormData` にパースします。`FormData.Fields` と `Files` は、フィールドとファイルの順序を保ちます。`FormData.Get` は最初のフィールド値、`GetAll` はすべての値、`File` は最初のバッファ済み `FormFile` を返します。`FormFile` は `Name`、パスを除いた `FileName`、`ContentType`、`Content` を公開します。

```csharp
app.Post("/profile", async c =>
{
    var form = await c.Req.Form();
    var name = form.Get("name") ?? "anonymous";
    var avatar = form.File("avatar");
    return c.Text($"{name}:{avatar?.FileName ?? "none"}");
});
```

`await c.Req.Multipart()` はフォーム全体をバッファせず、順番に読む `MultipartReader` を開きます。各 `MultipartPart` を `ReadNextAsync` で読み、次の part を読む前にその `Body` を読み切るか完了させます。part にはフィールドの `Name`、`FileName`、`ContentType`、ヘッダー、ストリーミング用の `PipeReader Body` があります。

例の `System.IO.Stream.Null` は、アプリケーションが選んで開いた保存先の代わりに置いています。

```csharp
app.Post("/upload", async c =>
{
    var multipart = await c.Req.Multipart();
    while (await multipart.ReadNextAsync(c.Aborted) is { } part)
    {
        if (part.FileName.Length != 0)
        {
            await part.Body.CopyToAsync(System.IO.Stream.Null, c.Aborted);
        }
        else
        {
            await part.Body.CompleteAsync();
        }
    }

    return c.Text("uploaded");
});
```

`AppOptions.MaxFormBodyBytes`（10 MiB）は `Form` がバッファする本文の上限、`MaxFormFields`（1,024）はフィールド数とアップロードファイル数それぞれの上限、`MaxMultipartParts`（1,024）は part 数の上限です。すべてのリクエスト本文には `MaxRequestBodyBytes`（30 MiB）も適用されます。`Multipart` には `MaxFormBodyBytes` は適用されませんが、`MaxRequestBodyBytes` と `MaxMultipartParts` は適用されます。直接フォームをパースした場合、対応しないメディアタイプは 415、不正な入力は 400、サイズ上限超過は 413 になります。

### Server-sent events

`c.EventStream` は `Content-Type: text/event-stream`、`Cache-Control: no-cache`、`X-Accel-Buffering: no` を設定し、`SseWriter` が書いた各イベントを flush します。

```csharp
app.Get("/events", c => c.EventStream(async (events, cancellationToken) =>
{
    await events.Send("connected", eventName: "status", id: "1");
    await events.Retry(TimeSpan.FromSeconds(5));
    await events.Comment("keep-alive");
}));
```

`Send` は payload の各行を 1 つずつ `data` 行として書き、イベント名と ID も指定できます。`Comment` は SSE comment、`Retry` はミリ秒単位の正の再接続間隔を書きます。接続が閉じたときは、コールバックのキャンセルトークンで処理を止めます。

### WebSocket

`c.WebSocket` は HTTP/1.1 の GET upgrade と HTTP/2 の extended CONNECT の両方を受け付けます。エンドポイントは `app.Get` で登録します。`WebSocketOptions.SubProtocols` はサーバー側の優先順で、クライアントが要求した中に一致するものがあれば選び、一致しなければ subprotocol なしで応答します。`KeepAliveInterval` の既定値は 30 秒です。

```csharp
using System.Net.WebSockets;

app.Get("/echo", c => c.WebSocket(async (socket, cancellationToken) =>
{
    var buffer = new byte[1024];
    var received = await socket.ReceiveAsync(buffer, cancellationToken);
    if (received.MessageType != WebSocketMessageType.Close)
    {
        await socket.SendAsync(
            buffer.AsMemory(0, received.Count),
            received.MessageType,
            received.EndOfMessage,
            cancellationToken);
    }
}, new WebSocketOptions
{
    SubProtocols = ["chat"]
}));
```

ハンドラーには接続済みの `System.Net.WebSockets.WebSocket` とリクエスト中断用のトークンが渡されます。ソケットが開いたままハンドラーが戻ると、Miya は正常終了します。ハンドラーで例外が発生すると、1011 の close を試みてから接続を中断します。

### HTML の補間

`c.Html($"...")` の補間文字列オーバーロードは、リテラルをそのまま書き、補間した値の HTML エスケープを行います。エスケープ対象は `&`、`<`、`>`、`"`、`'` です。明示的な opt-out API は `RawHtml.From(markup)` で、信頼済みの値をそのまま書きます。公開 API に `Html.Raw` メンバーはありません。

```csharp
app.Get("/hello", c =>
{
    var name = c.Query("name") ?? "guest";
    return c.Html($"<p>Hello, {name}</p>");
});
```

`Html(string)` オーバーロードは raw です。補間文字列をいったん `string` 変数へ代入し、その変数を `c.Html` に渡すと内容はエスケープされません。信頼できない値は補間の穴に置き、`RawHtml.From` は安全な markup にだけ使います。

## 静的ファイル

`app.Static` は、ファイルシステムのディレクトリまたは埋め込みリソースの prefix に対する GET ルートを登録します。`StaticOptions.Root` と `StaticOptions.Source` のどちらか一方だけを設定します。`Index` の既定値は `index.html` で、空文字列にするとディレクトリ index を無効にできます。`CacheControl` はすべての静的レスポンスに同じキャッシュポリシーを追加し、`Precompressed` は既定でファイルシステムの `.br` と `.gz` の隣接ファイルを有効にします。

```csharp
app.Static("/assets", new StaticOptions
{
    Root = "wwwroot",
    CacheControl = "public, max-age=3600",
    Precompressed = true
});
```

ファイルシステムのパスは、設定した root の下に字句的に収まるかを検査します。バックスラッシュ、rooted path、drive-qualified path、`.` と `..` のセグメントは拒否し、root 内の symlink は許可します。ディレクトリは、リクエストパスが `/` で終わる場合、または静的 root 自体を指す場合にだけ index ファイルを返します。見つからないパスや拒否されたパスは、アプリの `NotFound` ハンドラーで処理します。

ファイルシステムのレスポンスは `Last-Modified`、ETag、`If-None-Match` と `If-Modified-Since` による条件付きリクエストに対応し、`Accept-Ranges: bytes` を通知します。満たせる byte range が 1 つなら `Content-Range` 付きの 206、満たせない range なら 416 です。複数 range や解釈できない range は全体のレスポンスに戻ります。`If-Range` は range を使うかどうかを決めます。`Accept-Encoding` が許せば `.br` を `.gz` より優先し、元のファイルの content type を保ったまま `Content-Encoding` と `Vary: Accept-Encoding` を追加します。

埋め込みリソースは ETag と条件付きリクエストを使いますが、`Last-Modified`、range の通知や処理、圧縮済みの隣接ファイルは使いません。`/` を含むリソース名は、設定した prefix の後でそのままマッピングされます。MSBuild の既定の dotted resource name は、最後の拡張子区切りを残して、それより前のドットをディレクトリ区切りにします。MSBuild は `-` を `_` に変えることがあるため、URL でハイフンを保つには明示的な `LogicalName` を指定します。

```xml
<ItemGroup>
  <EmbeddedResource Include="wwwroot/index.html"
                    LogicalName="MyAssets/index.html" />
  <EmbeddedResource Include="wwwroot/app.js"
                    LogicalName="MyAssets/app.js" />
</ItemGroup>
```

```csharp
app.Static("/assets", new StaticOptions
{
    Source = StaticSource.Embedded(typeof(Program).Assembly, "MyAssets")
});
```

## インプロセスクライアントによるテスト

`app.Request(method, target, options)` はサーバーを起動せず、アプリケーションのパイプライン全体にリクエストを送ります。メソッドは大文字に正規化され、target には query string を含められます。ストリーミングされた本文も `TestResponse` にすべて集められます。`TestRequestOptions` では byte の `Body` または UTF-8 の `TextBody` を指定でき、空でない `Body` と `TextBody` は同時に指定できません。複数の `Headers` も指定できます。`TestResponse` は `Status`、順序を保った重複可能な `Headers`、`Body`、`Header`、`HeaderValues`、`Text`、codec を登録した場合の `Json<T>` を公開します。`Date` や `Server` など Kestrel が transport 層で追加するヘッダーは含まれません。

```csharp
using Miya;
using Xunit;

public sealed class UserTests
{
    [Fact]
    public async Task GetsUser()
    {
        var app = new App();
        app.Get("/users/:id", static c => c.Text(c.Param("id")));

        var response = await app.Request("GET", "/users/42");

        Assert.Equal(200, response.Status);
        Assert.Equal("42", response.Text());
    }
}
```

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

`Miya.Schema` は、ルートパラメータ、クエリ、ヘッダー、フォームフィールド、JSON body のフィールドを 1 つの入力 record にまとめます。パースと検証が成功した場合だけハンドラーを呼びます。

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

### Form binding

`.Form(input => input.Field)` を使うと、`await c.Req.Form()` からフィールドを読みます。生成される binder はプロパティ名を使って `FormData.Get` を呼ぶため、そのフィールドの最初の値を使います。URL-encoded と multipart のフォームフィールドに対応します。`Miya.FormFile` は生成されるフィールド型には使えません。アップロードには `FormData.File` またはストリーミング用の `MultipartReader` API を使います。

```csharp
var formSchema = Schemas.For<CreatePersonInput>()
    .Form(input => input.Name, rules => rules.NotEmpty().MaxLength(80))
    .Form(input => input.Age, rules => rules.Range(0, 120));

app.Post("/people", formSchema,
    static (c, input) => c.Json(input));

public sealed record CreatePersonInput(string Name, int Age);
```

同じ schema で `.Form` と `.Body` を併用できません。その場合、ジェネレーターは MIYA016 を報告します。生成された型付きエンドポイントでフォームのパースに失敗した場合は、エンドポイントの構造化された検証エラーとして status 400 を返します。`c.Req.Form()` を直接呼んだ場合は入力に応じた status を保ちます。対応しないメディアタイプは 415、不正な入力は 400、フォームの上限超過は 413 です。

### テキストのパース規則

生成されるテキストバインディングは invariant culture と厳密な形式を使います。整数型は先頭の符号だけを任意で受け付けます。`float` と `double` は先頭の符号、小数点、指数を受け付けます。`decimal` は先頭の符号と小数点を受け付けますが、指数と桁区切りは受け付けません。Boolean は `Boolean.TryParse` を使い、enum の名前は大文字と小文字を区別し、数値の enum 値も受け付けます。`char` は 1 文字でなければなりません。

`DateTime` は `yyyy-MM-dd`、`yyyy-MM-ddK`、`yyyy-MM-dd'T'HH:mm:ss`、`yyyy-MM-dd'T'HH:mm:ssK`、`yyyy-MM-dd'T'HH:mm:ss.FFFFFFF`、`yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK`、round-trip の `O` 形式を受け付けます。`DateTimeOffset` は offset なしの `yyyy-MM-dd`、`yyyy-MM-dd'T'HH:mm:ss`、`yyyy-MM-dd'T'HH:mm:ss.FFFFFFF` を受け付け、その値を UTC として扱います。offset または zone 付きでは `yyyy-MM-ddK`、`yyyy-MM-dd'T'HH:mm:ssK`、`yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK`、`O` を受け付けます。

`Pattern(regex)` はまず `RegexOptions.NonBacktracking` と culture-invariant を指定した正規表現を作ります。このオプションで扱えない式の場合は、culture-invariant の正規表現と 1 秒の match timeout に切り替えます。match timeout は検証失敗として扱います。

### Schema part の共有

interface または base type が宣言するフィールドについて `Schemas.Part<TPart>()` で再利用可能な schema part を定義し、`.Use(part)` で適用します。`Use<T, TPart>` 拡張メソッドには `where T : TPart` があるため、その part の宣言を実装または継承した具体的な入力にだけ適用できます。具体的な schema に同じメンバーの割り当てがあれば、part より優先されます。複数の part が同じメンバーを提供すると競合になり、MIYA024 が出ます。part の型は同じ compilation に 1 つだけ定義し、メンバーは暗黙的に実装する必要があります。違反には MIYA017 から MIYA019 が出ます。

```csharp
public interface IPageQuery
{
    int Page { get; }
}

public sealed record SearchOptions(string Query);
public sealed record SearchInput(int Page, SearchOptions Options) : IPageQuery;

var pagePart = Schemas.Part<IPageQuery>()
    .Query(input => input.Page, rules => rules.Default(1).Range(1, 50));

var searchSchema = Schemas.For<SearchInput>()
    .Query(input => input.Page, rules => rules.Range(1, 10))
    .Body(input => input.Options)
    .Use(pagePart);
```

### Rule method の共有

ネストした record に適用する規則を共有する場合は、`Rule<T>` の引数を起点とする 1 本の rule chain を含む static method を rules 引数に渡します。method group のまま渡すことも、転送用 lambda 経由で渡すこともできます。生成コードから参照される predicate と、それを含む型や必要なメンバーは `internal` または `public` でなければなりません。private predicate には MIYA026 が出ます。

```csharp
public sealed record Address(string City);
public sealed record Profile(string Name, Address Address);
public sealed record CreateProfileInput(Profile Profile);

public static class ProfileRules
{
    public static void Apply(Rule<Profile> rule) =>
        rule.Must(ProfileRules.HasName, "name must not be empty")
            .Must(ProfileRules.HasCity, "city must not be empty");

    public static bool HasName(Profile value) => value.Name.Length != 0;
    public static bool HasCity(Profile value) => value.Address.City.Length != 0;
}

var profileSchema = Schemas.For<CreateProfileInput>()
    .Body(input => input.Profile, ProfileRules.Apply);
```

method 自体は同じ compilation にある static method で、1 本の chain を含む必要があります。複数文の method、instance method、別の `Rule<T>` を起点にした chain、別 assembly の method には MIYA025 が出ます。

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

### Middleware factory と型付き App

組み込みの factory は `Miya.Middleware` namespace にあり、`Middleware<Context>` を返します。返された delegate を `app.Use(...)` に登録します。

```csharp
using Miya.Middleware;

app.Use(RequestId.Middleware());
```

`App<TContext>` では、`App<TContext>` の adapter 拡張メソッドも `Middleware<Context>` を受け取り、型付きコンテキストを通して実行します。adapter を使う middleware は、`next` に同じコンテキストインスタンスを渡さなければなりません。別のインスタンスを渡すと例外になります。`Middleware<TContext>` にはインスタンスの `Use` オーバーロードが直接使われ、`Middleware<Context>` には adapter が使われます。

### RequestLogger

`RequestLogger.Middleware()` は、リクエスト完了後に `METHOD PATH STATUS elapsedms` を書きます。既定の writer は `Console.Out` で、別の `TextWriter` には `RequestLoggerOptions.Writer` を設定します。パイプラインが例外を投げた場合は status 500 を記録してから再 throw します。

```csharp
app.Use(RequestLogger.Middleware());
```

### RequestId

`RequestId.Middleware()` は `X-Request-Id` を使い、既定では入力された値を信頼します。信頼できる値は、1 から 128 文字で、英数字、`.`、`_`、`-` だけを含みます。それ以外の値は 32 文字の小文字 hexadecimal の新しい ID に置き換えます。`RequestIdOptions.HeaderName` または `TrustIncoming` で変更できます。generic factory は選んだ値を `IRequestIdContext.RequestId` に保存します。

```csharp
app.Use(RequestId.Middleware(new RequestIdOptions { TrustIncoming = false }));
```

### SecureHeaders

`SecureHeaders.Middleware()` は、ハンドラーが設定していないヘッダーを補います。ハンドラーが設定した値が優先されます。既定値は `X-Content-Type-Options: nosniff`、`X-Frame-Options: SAMEORIGIN`、`Referrer-Policy: no-referrer`、`Strict-Transport-Security: max-age=15552000; includeSubDomains`、`X-XSS-Protection: 0`、`Cross-Origin-Opener-Policy: same-origin`、`Cross-Origin-Resource-Policy: same-origin`、`X-Permitted-Cross-Domain-Policies: none`、`X-Download-Options: noopen` です。Content Security Policy は既定では省略します。各オプションを null にすると、そのヘッダーを省略します。ストリーミング開始後はヘッダーを追加しません。

```csharp
app.Use(SecureHeaders.Middleware(new SecureHeadersOptions
{
    ContentSecurityPolicy = "default-src 'self'"
}));
```

### Cors

`Cors.Middleware` は origin を完全一致かつ大文字小文字を区別して比較します。origin の既定リストは空で、method の既定値は `GET`、`POST`、`PUT`、`DELETE`、`PATCH`、`HEAD`、`OPTIONS` です。header の既定リストも空です。header リストが空の場合、preflight の `Access-Control-Request-Headers` に安全な値があればそれを返します。`Credentials` の既定値は false、`MaxAge` は未設定です。origin のリストに `*` を含めるとすべての origin を許可して `*` を返しますが、credentials とは併用できません。

```csharp
app.Use(Cors.Middleware(new CorsOptions
{
    Origins = ["https://app.example"],
    Methods = ["GET", "POST"],
    Headers = ["Content-Type", "X-Request-Id"],
    ExposeHeaders = ["X-Request-Id"],
    Credentials = true,
    MaxAge = TimeSpan.FromMinutes(10)
}));
```

一致する preflight には 204 を返し、次の handler は呼びません。middleware pipeline に登録すると、router が自動で返す `OPTIONS` のレスポンスより前に preflight を処理できます。一致する通常のリクエストは次の handler を実行してから CORS ヘッダーを受け取ります。origin がない場合や一致しない場合は、CORS ヘッダーを付けずに次へ進みます。

### BasicAuth

`BasicAuth.Middleware` は、固定の `Username` と `Password` の組み合わせ、または `Validate` callback のどちらか一方を必須にします。既定の realm は `Restricted` です。認証情報がない場合や不正な場合は、UTF-8 の Basic challenge 付き 401 を返し、次の handler を呼びません。固定の認証情報は fixed-time comparison で比較し、password には colon を含められます。generic factory はデコードした username を `IAuthContext.AuthUser` に保存します。

```csharp
app.Use(BasicAuth.Middleware(new BasicAuthOptions
{
    Username = "admin",
    Password = "s3cret"
}));
```

### BearerAuth

`BearerAuth.Middleware` は、固定の `Token` または `Validate` callback のどちらか一方を必須にします。既定の realm は `Restricted` で、token には RFC 6750 の `b64token` 文字セットを使います。authorization がない場合や別の scheme の場合は 401 です。Bearer header の形式が壊れている場合は `error="invalid_request"` 付き 400、検証で拒否された token は `error="invalid_token"` 付き 401 です。generic factory は検証済みの token 文字列を `IAuthContext.AuthUser` に保存します。この middleware は bearer token を比較するだけで JWT を検証しません。JWT には `Miya.Jwt` を使います。

```csharp
app.Use(BearerAuth.Middleware(new BearerAuthOptions
{
    Token = "demo-token"
}));
```

### Csrf

`Csrf.Middleware()` は、form-like な content type を持つ安全でないメソッドの `Origin` ヘッダーを検査します。対象は空または未指定の type、`application/x-www-form-urlencoded`、`multipart/form-data`、`text/plain` です。GET、HEAD、OPTIONS は検査せず、JSON リクエストも通します。既定では `null` 以外の Origin を必須にし、HTTP または HTTPS の authority とリクエストの `Host` ヘッダーを大文字小文字を無視して比較します。scheme は比較しません。`CsrfOptions.Origins` は完全一致で大文字小文字を区別する許可 origin、`ValidateOrigin` は callback です。拒否したリクエストは 403 を返し、次の handler を呼びません。

```csharp
app.Use(Csrf.Middleware(new CsrfOptions
{
    Origins = ["https://app.example", "https://admin.example"]
}));
```

### Compression

`Compression.Middleware()` は、1,024 byte 以上のバッファ済みレスポンスを、既定の `CompressionLevel.Fastest` で Brotli または gzip に圧縮します。text、JSON、JavaScript、SVG、XML、WebAssembly の content type を対象にし、`Accept-Encoding` の quality 値を扱い、同じ quality なら Brotli を優先します。圧縮後の byte 数が小さい場合だけレスポンスを置き換え、`Content-Encoding` と `Vary: Accept-Encoding` を追加します。ストリーミングまたはバッファ上限を超えて移行したレスポンス、本文のない status、既存の `Content-Encoding`、`Content-Range`、`ETag` があるレスポンスは対象外です。

```csharp
app.Use(ETag.Middleware());
app.Use(Compression.Middleware(new CompressionOptions { MinBytes = 512 }));
```

上のように ETag を Compression より前に登録します。Compression が選んだ表現を ETag が後から見て、圧縮済みの byte 列に対する ETag になります。

### ETag

`ETag.Middleware()` は GET と HEAD の、本文が空でないバッファ済み 200 レスポンスに strong entity tag を追加します。ハンドラーが設定した `ETag` は残します。一致する `If-None-Match` があれば、レスポンスを本文なしの 304 に変えます。生成する tag を weak にする場合は `ETagOptions.Weak = true` を設定します。ストリーミング中またはバッファ上限を超えて移行したレスポンスは対象外です。

```csharp
app.Use(ETag.Middleware(new ETagOptions { Weak = true }));
```

### RequestTimeout

`RequestTimeout.Middleware(timeout)` に既定の timeout はありません。正の期限を過ぎた時点でレスポンスがまだバッファされていれば、`text/plain; charset=utf-8` の本文 `Gateway Timeout` と status 504 に置き換えます。ストリーミングが始まった後は status を変えられないため、Miya は接続を中断します。

```csharp
app.Use(RequestTimeout.Middleware(TimeSpan.FromSeconds(2)));
```

### バッファ済みレスポンスの hook

middleware の作者は `TryGetBufferedResponse(out var body)` で空でないバッファ済みレスポンスを調べ、`ReplaceBufferedResponse(body, contentType)` で置き換えられます。どちらもレスポンスの送信またはストリーミング開始前だけ使えます。`TryGetBufferedResponse` が返す memory は、レスポンスを置き換えるかリクエストが終わるまで有効です。`AppOptions.MaxBufferedResponseBytes` を超えて自動的にストリーミングへ移行した後は false を返し、本文がない場合も false を返します。本文を禁止する status では置き換えた byte 列を破棄します。HEAD では middleware の検査用に置き換えた内容を保持しますが、送るのは長さだけです。

```csharp
app.Use(async (c, next) =>
{
    await next(c);
    if (c.TryGetBufferedResponse(out var body))
    {
        var replacement = body.ToArray();
        c.ReplaceBufferedResponse(replacement);
    }
});
```

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

リクエストのパースには、壊れた入力や悪意ある JSON がメモリやスタックを使い尽くせないよう上限を設けます。既定値はネットワークからの入力を想定したもので、`AppOptions` と `JsonOptions` で設定します。レスポンスのシリアライズは single-pass です。入力ドキュメント、文字列、コレクション、数値の上限はレスポンスに適用されません。レスポンス JSON には、設定した最大深度、非有限数の扱い、プールに保持するバッファの設定、キャンセルトークンが適用されます。

| 設定 | 既定値 |
| --- | ---: |
| JSON リクエスト本文、`AppOptions.MaxJsonBodyBytes` | 1 MiB |
| 入力 JSON ドキュメント全体、`MaxDocumentByteLength` | 1 MiB |
| オブジェクトと配列の深さ、`MaxDepth` | 64 |
| 1 つの入力文字列トークン、`MaxStringByteLength` | 1 MiB |
| 1 つの入力オブジェクトのメンバー数または 1 つの配列の要素数、`MaxCollectionSize` | 1,048,576 |
| 1 つの入力数値の桁数、`MaxNumberDigits` | 128 |
| プールに保持する JSON 一時バッファ、`MaxPooledBufferByteLength` | 64 KiB |
| chunked へ移行する前のレスポンスバッファ、`AppOptions.MaxBufferedResponseBytes` | 1 MiB |
| リクエスト本文、`AppOptions.MaxRequestBodyBytes` | 30 MiB |

NaN と Infinity は既定で拒否します。`JsonOptions` は、時間のかかるシリアライズとパースのためのキャンセルトークンも持ちます。

`AppOptions.MaxBufferedResponseBytes` はレスポンスバッファのしきい値であり、出力 JSON の上限ではありません。Miya はレスポンスのシリアライザーを 1 回で実行し、バッファがしきい値を超えると、そこまでに書いた byte 列を chunked streaming に移し、残りの JSON をそのまま書き続けます。しきい値を超えたことだけを理由にレスポンスを拒否することはありません。

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

### HTTP クライアントの生成

`miya-gen client` mode は、OpenAPI ドキュメントから型付き `HttpClient` wrapper を生成します。`--namespace` の既定値は `Generated`、`--class-name` の既定値はドキュメントの title から作った名前です。

```sh
miya-gen client --input api/openapi.json --output Generated \
  --namespace MyApp.Api --class-name CatalogClient
```

生成される `CatalogClient` は、`CatalogClient(HttpClient http)` コンストラクターを持つ public sealed class です。対応する operation ごとに、path、必須または任意の query と header parameter、定義されていれば JSON request body、最後に任意の `CancellationToken` を持つ async method を生成します。JSON の成功レスポンス本文があれば `Task<T>` を返し、レスポンス本文がなければ `Task` を返します。JSON 以外の成功レスポンス本文は表現しません。HTTP の成功以外のレスポンスでは `ApiException` を投げます。`ApiException` は `Status` と、UTF-8 で最大 4,096 byte に切り詰めた `Body` を公開します。

コンパイラのジェネレーターでクライアントを生成する場合は、`AdditionalFiles` に別の metadata を指定します。

```xml
<ItemGroup>
  <AdditionalFiles Include="api/openapi.json"
                   MiyaOpenApiClient="true"
                   MiyaOpenApiNamespace="MyApp.Api"
                   MiyaOpenApiClientName="CatalogClient" />
</ItemGroup>
```

`MiyaOpenApiClient` は server import とは独立してクライアント生成を有効にします。`MiyaOpenApiNamespace` は対象 namespace、`MiyaOpenApiClientName` は class 名を指定します。省略した場合はプロジェクトの root namespace と OpenAPI title から作った名前を使います。同じファイルに `MiyaOpenApi="true"` と `MiyaOpenApiClient="true"` を設定すると、server import と client が生成する component 宣言を共有します。生成クライアントが扱えるのは JSON の成功レスポンス本文だけで、対応しない operation は generator の診断を出してスキップします。

ソースジェネレーターを使えないビルド構成のために、`miya-gen import` が同じ取り込みを手動の一手順として行います。生成した `.g.cs` をコンパイルに入れるのではなくディスクに書き出します。

```sh
miya-gen import --input api/openapi.json --output Generated --namespace MyApp.Api
```

## Miya.Jwt

`Miya.Jwt` パッケージは reflection を使わずに compact JWT を署名・検証します。HS256、RS256、ES256 に対応します。`Jwt.Sign` は `JwtKey` が選んだアルゴリズムで token を作り、`Jwt.Verify` は署名と登録済み claim を検証してから、不正な token では例外を投げず `JwtResult` を返します。

```xml
<PackageReference Include="Miya.Jwt" Version="0.1.0" />
```

```csharp
using Miya.Jwt;

var key = JwtKey.HS256("01234567890123456789012345678901"u8);
var token = Jwt.Sign(
    new JwtPayload
    {
        Subject = "alice",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
    },
    key);

var result = Jwt.Verify(token, key);
if (result.IsValid)
{
    Console.WriteLine(result.Payload!.Subject);
}
```

`JwtKey.HS256` は 32 byte 以上の secret をコピーします。`JwtKey.RS256` は 2048 bit 以上の RSA key、`JwtKey.ES256` は NIST P-256 の ECDSA key を受け付けます。`JwtPayload` は登録済み claim を持ち、`WithClaim` で string、integer、Boolean の scalar claim を追加できます。

`JwtValidation` では、完全一致させる `Issuer`、token に含める `Audience`、`ClockSkew`（既定は 60 秒）、`RequireExpiration`（既定は true）、`Clock` を設定できます。検証で受け付けるアルゴリズムは渡した key に固定されます。`none`、未知のアルゴリズム、key と一致しないアルゴリズムの token は署名検証前に拒否します。

`JwtAuth.Middleware` は、次の handler を呼ぶ前に bearer token を検証します。token がない場合や不正な場合は Bearer challenge 付き 401 を返します。`JwtAuthOptions.Key` は必須で、`Validation` は任意、`Realm` の既定値は `Restricted` です。generic overload は `IJwtContext` を実装したコンテキストを要求し、検証済みの `JwtPayload` をその `Jwt` プロパティに保存します。

```csharp
using Miya;
using Miya.Jwt;

public sealed class ApiContext : Context, IJwtContext
{
    public JwtPayload? Jwt { get; set; }
}

var api = new App<ApiContext>();
api.Use(JwtAuth.Middleware<ApiContext>(new JwtAuthOptions { Key = key }));
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

`Run(int? port = null)` は HTTP/1.1 リスナーを起動し、キャンセルまたは終了シグナルまでブロックします。アドレスを設定せず `HOST` に IP アドレスもない場合は loopback に bind します。`Run()` はポートを未指定にするので `PORT` 環境変数が有効になります。`Run(8080)` はポートを明示的に選びます。`RunAsync(options, ct)` と `StartAsync(options, ct)` は非同期でホストします。`StartAsync` は、バインドしたアドレスと `StopAsync` を持つ `Server` を返します。ポート 0 は OS に空きポートを要求します。

ポートの選択は、まず明示的な `Run(port)` の値、次に `AppOptions.Port`、次に `PORT` に入った妥当な整数、最後に 3000 を使います。明示的またはオプションで渡された 0 から 65535 の範囲外の値は拒否します。`PORT` が不正な値のときは無視します。

`AppOptions.Address` は bind するアドレスを選び、`HOST` より優先されます。省略した場合、`HOST` に IP アドレスが入っていればそれを使い、それ以外は loopback を使います。コンテナの外からアクセスを受ける場合は、`Address = IPAddress.Any` または `HOST=0.0.0.0` を設定します。`IPAddress.IPv6Any` は dual-stack の listener を bind します。

```csharp
using System.Net;

await app.RunAsync(new AppOptions
{
    Address = IPAddress.Any,
    Port = 8080
});
```

SIGINT、SIGTERM、キャンセルは新規リクエストの受付を止め、処理中のものを待ちます。既定のシャットダウンタイムアウトは 30 秒です。2 回目のシグナルはプロセスを即座に終了します。

Windows でも同じ graceful shutdown の signal registration を使います。Ctrl+C と終了要求は新しい処理の受付を止め、設定した timeout の範囲で実行中のリクエストを待ちます。

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

Miya v0 は、テンプレート、開発用証明書の探索、設定ファイル連携を提供しません。HTTP/3 は `QuicListener.IsSupported` と渡した証明書に依存します。TLS 終端のためのリバースプロキシは選択肢として使えます。

ルートのジェネレーターは、リテラルのパターンをコンパイル時に検証・解析し、解析済みのテンプレートを埋め込みます。ランタイムは起動時にそれらからセグメント単位の trie を構築して照合します。ジェネレーターはルート単位の照合コードは出力しません。

診断 MIYA001 から MIYA004 は JSON とルートの生成を扱います。MIYA006 はリテラルの `c.Param` 呼び出しをハンドラーのルートと照合します。MIYA010 から MIYA015 は、型付き入力のルート割り当て、対応するフィールド型、スキーマ定義、検証規則、競合する割り当てを扱います。MIYA016 はフォームと JSON body の割り当てを併用した場合に出ます。MIYA017 から MIYA019 は、重複または未宣言の schema part と明示的な interface 実装を扱います。MIYA020 から MIYA023 は、不正な OpenAPI ドキュメント、未対応の schema 構造、Miya に変換できない値、生成名の衝突を扱います。MIYA024 から MIYA026 は、schema part のメンバー競合、共有 rule declaration の不正、predicate のアクセス不可を扱います。プールされる派生コンテキストで消し忘れたフィールドを検出する MIYA005 は未実装なので、`IPoolableContext.OnReturn()` でそれらを消すのは呼び出し側の責任のままです。

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
