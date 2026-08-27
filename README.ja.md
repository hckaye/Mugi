# Miya

[English](README.md) | 日本語

Miya は NativeAOT を前提に設計した .NET 10 向けの小さな HTTP フレームワークです。アプリケーションに `WebApplication`、Generic Host、DI コンテナは必要ありません。平文の HTTP/1.1 と HTTP/2 では Kestrel を直接構築し、TLS と HTTP/3 のときだけ Kestrel の組み込みサービス登録を内部で使います。実行時のリフレクション、アセンブリスキャン、実行時コード生成は行いません。ハンドラーは属性付きのコントローラーメソッドではなくラムダで書きます。

ルートテンプレートと JSON codec はコンパイル時に生成します。ルーティングのジェネレーターはリテラルのパターンを検証し、解析済みのテンプレートを埋め込みます。v0 では照合そのものは共有のランタイムマッチャーが行います。生成された JSON codec は module initializer で自身を登録します。リクエストの API は `Text`、`Json`、`Param`、`Query` を持つコンテキストモデルです。ミドルウェアは選ばれたルートを包む onion 順で合成します。

## 動作要件とプロジェクト設定

Miya は `net10.0` を対象にします。以下の計測には .NET SDK 10.0.203 を使いました。

### NuGet パッケージ参照

ローカルで pack または publish したパッケージを使うアプリケーションは、ランタイムとジェネレーターの両方を参照します。

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

`Miya.Generators` パッケージには analyzer アセンブリと `buildTransitive` の props ファイルが入っています。props ファイルは analyzer と `Miya.Generated` インターセプター名前空間を追加します。パッケージがプロジェクト参照を経由して届く場合も同じです。上記の明示的な `InterceptorsNamespaces` プロパティは、パッケージ参照とソース参照を切り替えるときにも有効です。

### プロジェクト参照

リポジトリ内のプロジェクトは `Miya.Generators` を analyzer としてコンパイラに渡します。

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <PublishAot>true</PublishAot>
  <InterceptorsNamespaces>$(InterceptorsNamespaces);Miya.Generated</InterceptorsNamespaces>
</PropertyGroup>

<ItemGroup>
  <ProjectReference Include="../Miya/src/Miya/Miya.csproj" />
  <ProjectReference Include="../Miya/src/Miya.Generators/Miya.Generators.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

`MiyaJsonNaming` は生成するプロパティ名の規約を選びます。既定は `camelCase` です。C# のプロパティ名の大文字小文字をそのまま使うには `<MiyaJsonNaming>PascalCase</MiyaJsonNaming>` を設定します。

## クイックスタート

```csharp
using System.Diagnostics;
using System.Globalization;
using Miya;

var app = new App();

app.Use(static async (context, next) =>
{
    var stopwatch = Stopwatch.StartNew();
    await next(context);
    context.Header(
        "Server-Timing",
        $"app;dur={stopwatch.Elapsed.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)}");
});

app.Get("/", static context => context.Text("Hello"));
app.Get("/users/:id", static context => context.Json(new User(context.Param("id"))));

app.Run();

internal sealed record User(string Id);
```

既定のポートで起動します。

```sh
dotnet run
curl -i http://127.0.0.1:3000/users/42
```

`PORT=8080 dotnet run` はプログラムを変えずに待受ポートを変えます。

## 型付きコンテキスト

`App<TContext>` は、文字列キーやキャストなしでアプリケーション固有のリクエスト状態を運びます。派生コンテキストはリクエストごとに新しく作られます。ただし `IPoolableContext` を実装した型はプールされます。プールされる派生コンテキストは `OnReturn()` で自分のフィールドを消す必要があります。

```csharp
using Miya;

var app = new App<MyContext>();

app.Use(static async (context, next) =>
{
    context.CurrentUser = new User(context.Req.Header("X-User") ?? "anonymous");
    await next(context);
});

app.Get("/me", static context => context.Json(context.CurrentUser));

public sealed class MyContext : Context
{
    public User? CurrentUser { get; set; }
}

public sealed record User(string Id);
```

ミドルウェアはルートの前に登録順で走り、`next(context)` の完了後に逆順で戻ります。`next` を複数回呼ぶと拒否されます。

## ルーティングの挙動

ルートのパターンは、静的セグメント、1 セグメントの `:name`、残りのパスを受ける `*name` で構成します。ワイルドカードは末尾のセグメントでなければなりません。各セグメントでは静的なテキストがパラメータより優先し、パラメータがワイルドカードより優先します。優先順位が同じルートは登録順になります。

`Get`、`Post`、`Put`、`Delete`、`Patch`、`Head`、`Options`、`All`、`On` がハンドラーを登録します。`Route(prefix, subApp)` は別のアプリケーションをマウントし、prefix と子ルートの接合部を正規化します。

パスが一致してメソッドが違う場合は 405 と `Allow` ヘッダーを返します。明示的な HEAD ルートがなければ、GET ルートが HEAD も処理し、GET のヘッダーと `Content-Length` を保ったまま本文を抑止します。パスが存在して明示的な OPTIONS ルートが処理しない場合、OPTIONS には `Allow` 付きの 204 を返します。どのルートにも一致しないパスは 404 になります。

照合は Kestrel がデコードした `Path` を ordinal 比較で使い、Unicode 正規化はしません。Kestrel は `%2F` のようなエンコードされたスラッシュをパスに残します。`Param()` は照合後にそれをデコードするので、`/items/a%2Fb` は `/items/:id` に一致し、`id` は `a/b` になります。不正なパーセントエスケープは 400 を返します。`/users` と `/users/` は別のルートで、v0 では両者の間でリダイレクトしません。

リテラルのパターンはジェネレーターが解析して検証します。動的な文字列から作ったルートは登録時に一度だけ解析され、照合の挙動は同じです。

## MiyaJson codec

MiyaJson の契約は、生成または手書きの `IMiyaJsonCodec<T>` です。生成された codec は module initializer によってジェネリックな静的領域に登録されます。`context.Json(value)`、`context.Req.Json<T>()`、`MiyaJson` の各エントリーポイントはその登録先を使います。アセンブリスキャンは関与しません。

コンパイラのインターセプターは、判明している呼び出し箇所を生成コードの直接呼び出しに置き換える最適化です。呼び出しがインターセプトされない場合でも、codec が登録済みであればシリアライズは動きます。ジェネリックヘルパー内の呼び出しや、別アセンブリでコンパイルされた呼び出しも同様です。具体型がジェネリックコードを通じてしか現れない場合は `MiyaJson.Include<T>()` で生成を要求します。

```csharp
MiyaJson.Include<User>();
```

codec が登録されていない場合、MiyaJson はジェネレーターの追加方法、`miya-gen` の使用、`Include<T>()` の呼び出し、手書き codec の登録のいずれかを案内する例外を投げます。

### 手書き codec の登録

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

生成される型モデルは、真偽値と数値のプリミティブ、`char`、`string`、`Guid`、`DateTime`、`DateTimeOffset`、`decimal`、数値の enum、nullable な値、一次元配列、`List<T>`、`Dictionary<string, T>` に対応します。public または internal の class、record、struct は、これらの型を再帰的に組み合わせられます。record は primary constructor が必要です。POCO クラスは public または internal のパラメーターなしコンストラクターが必要で、シリアライズするプロパティにはアクセス可能な get と set/init のアクセサーが必要です。

interface、`object`、ポリモーフィックな契約、クラス継承、匿名型、private メンバー、ref-like 型、開いたジェネリック型、多次元配列、string 以外をキーに持つ dictionary は、生成 codec の対象外です。

### 既定の上限

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

## miya-gen によるソース生成

`miya-gen` は、コンパイラ統合のソースジェネレーターが使えない環境で、同じ生成コアを使います。JSON codec、module initializer による登録、解析済みのルートテンプレートを通常の `.cs` ファイルとして書き出します。インターセプターは出力しないので、直接呼び出しの最適化だけが無効になります。

パッケージフィードからツールをインストールまたは更新し、プロジェクトに含まれるディレクトリへ生成します。

```sh
dotnet tool install --global Miya.Gen --version 1.0.0
dotnet build MyApp.csproj
miya-gen --project MyApp.csproj --output Generated
dotnet build MyApp.csproj
```

出力ディレクトリがプロジェクトのルート配下にあれば、SDK は `Generated/*.cs` を自動的に含めます。プロジェクトの外にあるディレクトリは `Compile` アイテムで追加します。このリポジトリでは、同等の生成コマンドは次のとおりです。

```sh
dotnet run --project src/Miya.Gen -- \
  --project samples/Hello/Hello.csproj \
  --output samples/Hello/Generated
```

生成の前にプロジェクトがコンパイルできる必要があります。出力ディレクトリにある既存の `Miya.*.g.cs` ファイルは置き換えられます。

## Kestrel によるホスティング

`Run(int? port = null)` は loopback の HTTP/1.1 リスナーを起動し、キャンセルまたは終了シグナルまでブロックします。`Run()` はポートを未指定のままにし、`PORT` 環境変数を有効にします。`Run(8080)` は明示的な指定です。他のプロトコルは `MiyaOptions.Protocols` と `MiyaOptions.Certificate` を `RunAsync` または `StartAsync` で設定します。

`RunAsync(MiyaOptions?, CancellationToken)` と `StartAsync(MiyaOptions?, CancellationToken)` は非同期のホスティングを提供します。`StartAsync` は、バインドしたアドレスと `StopAsync` を持つ `MiyaServer` を返します。ポート 0 は OS が割り当てるポートを要求します。

ポートの選択は、まず明示的な `Run(port)` の値、次に `MiyaOptions.Port`、次に `PORT` に入った妥当な整数、最後に 3000 を使います。0 から 65535 の範囲外の値は、明示的に渡された場合もオプションで渡された場合も拒否します。`PORT` が不正な値のときは無視して 3000 にフォールバックします。

SIGINT、SIGTERM、キャンセルは新規リクエストの受付を止め、処理中のリクエストを待ちます。既定のシャットダウンタイムアウトは 30 秒です。2 回目のシグナルはプロセスを即座に終了します。レスポンス本文は、1 MiB の既定値を超えるか `Stream` を使わない限り、ミドルウェアが戻るまでバッファに保持します。`next` の後のヘッダー変更は、レスポンスがバッファのままの間だけできます。

証明書がない場合、既定のプロトコルは HTTP/1.1 です。平文の HTTP/2 には `MiyaProtocols.Http2` を選びます。

```csharp
await app.RunAsync(new MiyaOptions
{
    Protocols = MiyaProtocols.Http2,
});
```

平文のリスナーは ALPN のネゴシエーションがないため、HTTP/1.1 と HTTP/2 を同時に提供できません。Miya はその組み合わせを起動時に拒否します。

`X509Certificate2` を渡すと、Miya 内で TLS を終端します。証明書を渡したときの既定は、ALPN で選ばれる HTTP/1.1 と HTTP/2 です。

```csharp
using System.Security.Cryptography.X509Certificates;

using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
    "server.pfx",
    "certificate-password");

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

HTTP/3 を要求しても `QuicListener.IsSupported` が false の場合、起動時に `PlatformNotSupportedException` を投げます。以下の計測に使った macOS arm64 環境では `QuicListener.IsSupported` は false を返しました。その条件では HTTP/3 の統合テストをスキップしました。HTTP/1.1 または HTTP/2 が HTTP/3 と同じエンドポイントを共有すると、Kestrel は `Alt-Svc` を自動的に有効にします。

`ConfigureKestrel` は、他の対応する Kestrel 設定のために使えます。証明書の指定は `MiyaOptions.Certificate` に置きます。Miya は開発用証明書を探したり、Kestrel のエンドポイント設定ファイルを読み込んだりはしません。

`MiyaOptions.ConfigureServices` は、内部の Kestrel ホストに追加のサービスを登録します。Miya が依存性注入を必要とすることはありません。このフックは Kestrel を高度にカスタマイズするためだけのものです。設定すると、平文のエンドポイントでもサービス経由のホスティングパスを選びます。登録したサービスはサーバー内部に留まり、ハンドラーやミドルウェアには届きません。

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

Miya v0 は、WebSocket のアップグレード、静的ファイルの配信、OpenAPI ドキュメントの生成に対応しません。認証、バリデーション、テンプレート、開発用証明書の探索、設定ファイル連携も提供しません。HTTP/3 は `QuicListener.IsSupported` と渡した証明書に依存します。TLS 終端のためのリバースプロキシは任意です。

ルートのジェネレーターは、v0 ではルート単位の照合コードや統合した trie を出力しません。リテラルのパターンをコンパイル時に検証・解析し、解析済みのテンプレートをランタイムマッチャーのために埋め込みます。

診断 MIYA001 から MIYA004 は、匿名 JSON 型、不正なルート、限定的な重複ルート検出、非対応の JSON 型を扱います。プールされる派生コンテキストで消し忘れたフィールドを検出する MIYA005 は未実装です。`IPoolableContext.OnReturn()` は呼び出し側の責任のままです。

## サードパーティー表記

サードパーティーの謝辞は [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) に記録しています。
