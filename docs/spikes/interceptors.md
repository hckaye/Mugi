# C# インターセプターと module initializer 登録の検証結果

検証日は 2026-08-27。環境は macOS arm64、.NET SDK 10.0.203、C# の `LangVersion` は `latest` です。SDK 同梱コンパイラーは Roslyn 5.3.0 系です。generator は `netstandard2.0` を対象にし、`Microsoft.CodeAnalysis.CSharp` 4.14.0 を `PrivateAssets="all"` で参照しました。

## 結果

| 項目 | 判定 | 実測結果 |
|---|---|---|
| 通常のメソッド呼び出し | 成立 | `SimpleTarget.Call` が `intercepted-call:*` を返した |
| 継承レシーバー | 成立 | `App` 変数から呼んだ `AppBase.Get` を差し替えた。`this` の型は `AppBase` が必須 |
| ジェネリックメソッド | 成立 | `MyContext` から呼んだ `Context.Json<T>` を具体型の転送メソッドへ差し替えた。`this` の型は `Context` が必須 |
| module initializer 登録 | 成立 | 生成した initializer が `Registry<T>.Instance` を設定し、差し替え不能なメソッドグループ経由でも codec を取得できた |
| NuGet の直接参照 | 成立 | generator パッケージを参照する `DirectLibrary` で 3 種類の差し替えが動いた |
| NuGet の推移参照 | 成立 | generator の参照を持たない `TransitiveApp` でも 3 種類の差し替えが動いた |
| NativeAOT | 成立 | IL/AOT 警告なし。生成した Mach-O arm64 バイナリが JIT 実行時と同じ結果を返した |

インターセプターを Miya の必須動作にせず、静的 codec 登録を通常本体から使う設計は成立します。直接呼び出せる場所だけをインターセプターで最適化できます。

## 検証プロジェクト

- `src/Miya.Interceptors.Spike.Runtime` は `AppBase.Get`、`Context.Json<T>`、`Registry<T>.Instance` などの最小 API を持ちます。
- `src/Miya.Interceptors.Spike.Generator` は `IIncrementalGenerator` の実装です。NuGet パッケージには generator DLL と `buildTransitive` の props を入れます。
- `package-tests/DirectLibrary` は runtime と generator のローカルパッケージを直接参照します。
- `package-tests/TransitiveApp` は `DirectLibrary` の `ProjectReference` だけを持ちます。runtime と generator の `PackageReference` はありません。
- `package-tests/ReceiverMismatch` は派生型を `this` パラメーターに使わせる負例です。CS9148 を期待します。

`package-tests/NuGet.config` の `spike-local` が `artifacts/packages` を参照します。NativeAOT toolchain の取得には `nuget.org` も残しています。`Miya.Interceptors.Spike.*` はローカルソースから復元されます。

## InterceptsLocation の生成

incremental pipeline は `InvocationExpressionSyntax` を候補にし、次の API で位置を取得します。

```csharp
var location = semanticModel.GetInterceptableLocation(invocation, cancellationToken);
var attributeSyntax = location.GetInterceptsLocationAttributeSyntax();
```

Roslyn 4.14.0 では `GetInterceptsLocationAttributeSyntax()` の戻り値は `string` です。得られた文字列は次の形式でした。ファイルパス、行、列を手作業で属性に入れる旧形式は使っていません。

```csharp
[global::System.Runtime.CompilerServices.InterceptsLocationAttribute(
    1,
    "nK4yYk+9id4e4LPNYqylINsBAABQcm9ncmFtLmNz")]
```

consumer 側には `InterceptsLocationAttribute(int version, string data)` の定義も生成します。.NET 10 の BCL からこの属性型を参照する設定は不要でした。

必要な MSBuild 設定は次のとおりです。

```xml
<PropertyGroup>
  <LangVersion>latest</LangVersion>
  <InterceptorsNamespaces>$(InterceptorsNamespaces);Miya.Interceptors.Spike.Generated</InterceptorsNamespaces>
</PropertyGroup>
```

`LangVersion=preview` と `Features=InterceptorsPreview` は不要でした。`InterceptorsNamespaces` を `Other.Namespace` に上書きして `Rebuild` すると、生成した 3 メソッドすべてが CS9137 になりました。

## 継承レシーバー

呼び出し側の変数は `App` ですが、`Get` の宣言元は `AppBase` です。成功した生成コードは `this AppBase` を使います。

```csharp
public static string Intercept_1(
    this global::Miya.Interceptors.Spike.Runtime.AppBase receiver,
    string pattern,
    global::Miya.Interceptors.Spike.Runtime.Handler handler)
    => receiver.InterceptedGet(pattern, handler);
```

generator では次のシンボルからレシーバー型を決めています。

```csharp
var receiverType = method.OriginalDefinition.ContainingType;
```

負例ではレシーバー式の見かけの型 `App` を使いました。コンパイラーは次のエラーを返しました。

```text
error CS9148: インターセプターには、'AppBase.Get(string, Handler)' で
'AppBase this' パラメーターと一致する 'this' パラメーターが必要です。
```

派生型から呼んだ場合も、インターセプターの第 1 引数はメソッド宣言元の型にする必要があります。

## ジェネリックメソッド

`MyContext : Context` のインスタンスから `context.Json(new DirectPayload(1))` を呼びました。生成した転送メソッドは非ジェネリックで、call site の具体型を引数に使います。

```csharp
public static string Intercept_2(
    this global::Miya.Interceptors.Spike.Runtime.Context receiver,
    global::DirectLibrary.DirectPayload value)
    => receiver.InterceptedJson<global::DirectLibrary.DirectPayload>(value);
```

この形でビルドと実行の両方が成功しました。`this MyContext` を使う負例は CS9148 になり、`Context.Json<MismatchPayload>` には `Context this` が必要だと報告されました。

具体型引数は `IMethodSymbol.TypeArguments[0]`、レシーバー型は `IMethodSymbol.OriginalDefinition.ContainingType` から取得できます。

## module initializer と差し替え不能な呼び出し

generator は `Json<T>` と `MiyaJson.Include<T>()` から型を集め、型ごとの `ICodec<T>` と次の登録コードを出力します。

```csharp
[global::System.Runtime.CompilerServices.ModuleInitializer]
internal static void Initialize()
{
    global::Miya.Interceptors.Spike.Runtime.MiyaJson.Register<DirectPayload>(
        new GeneratedCodec_0());
}
```

登録先は静的ジェネリックフィールドです。

```csharp
public static class Registry<T>
{
    public static ICodec<T>? Instance;
}

public static void Register<T>(ICodec<T> codec) => Registry<T>.Instance = codec;
```

`DirectLibrary` では次のメソッドグループも実行しました。これは `InvocationExpressionSyntax` の call site ではないため、インターセプターの対象になりません。

```csharp
MiyaJson.Include<DirectPayload>();
Func<DirectPayload, string> methodGroup = context.Json<DirectPayload>;
var result = methodGroup(new DirectPayload(2));
```

結果は `runtime-json:DirectLibrary.DirectPayload` でした。通常の `Context.Json<T>` 本体が `Registry<T>.Instance` を読み、生成した codec を取得しています。initializer が動かなければ例外になる実装なので、登録の実行も確認できています。`Main` では `Registry<DirectPayload>.Instance` と `Registry<TransitivePayload>.Instance` が null でないことも検査しました。

この結果から、直接呼び出し、メソッドグループ、delegate 変換を同じ静的登録で扱えます。ジェネリックヘルパーだけに現れる型は `MiyaJson.Include<T>()` のようなマーカーが必要です。

## NuGet と buildTransitive

generator パッケージには次の 2 ファイルを入れました。

```text
analyzers/dotnet/cs/Miya.Interceptors.Spike.Generator.dll
buildTransitive/Miya.Interceptors.Spike.Generator.props
```

generator の csproj で使った設定は次のとおりです。

```xml
<TargetFramework>netstandard2.0</TargetFramework>
<IncludeBuildOutput>false</IncludeBuildOutput>
<SuppressDependenciesWhenPacking>true</SuppressDependenciesWhenPacking>

<PackageReference Include="Microsoft.CodeAnalysis.CSharp"
                  Version="4.14.0"
                  PrivateAssets="all" />

<None Include="$(OutputPath)$(AssemblyName).dll"
      Pack="true"
      PackagePath="analyzers/dotnet/cs" />
<None Include="buildTransitive/Miya.Interceptors.Spike.Generator.props"
      Pack="true"
      PackagePath="buildTransitive/Miya.Interceptors.Spike.Generator.props" />
```

props は名前空間を有効にし、推移参照先にも analyzer を追加します。

```xml
<PropertyGroup>
  <InterceptorsNamespaces>$(InterceptorsNamespaces);Miya.Interceptors.Spike.Generated</InterceptorsNamespaces>
</PropertyGroup>
<ItemGroup>
  <Analyzer Include="$(MSBuildThisFileDirectory)../analyzers/dotnet/cs/Miya.Interceptors.Spike.Generator.dll" />
</ItemGroup>
```

`dotnet msbuild -getProperty:InterceptorsNamespaces -getItem:Analyzer` を両 consumer で実行すると、どちらも次の状態でした。

```text
InterceptorsNamespaces = ;Miya.Interceptors.Spike.Generated
Analyzer = .../miya.interceptors.spike.generator/0.1.0/analyzers/dotnet/cs/
           Miya.Interceptors.Spike.Generator.dll
DefiningProject = .../buildTransitive/Miya.Interceptors.Spike.Generator.props
```

`TransitiveApp` のソースにも `MiyaInterceptorSpike.g.cs` が生成されました。実行結果の `TRANSITIVE=intercepted-*` は、そのプロジェクト自身の call site が差し替わったことを示します。

## NativeAOT

アプリ側の設定は次のとおりです。

```xml
<TargetFramework>net10.0</TargetFramework>
<PublishAot>true</PublishAot>
<IsAotCompatible>true</IsAotCompatible>
```

`dotnet publish` は `Generating native code` まで完了し、IL2026、IL3050 などの IL/AOT 警告は出ませんでした。生成物は `Mach-O 64-bit executable arm64`、サイズは 1,236,096 bytes でした。

NativeAOT バイナリの出力は JIT 実行時と同じです。

```text
DIRECT=intercepted-call:direct|intercepted-get:/direct:handler|intercepted-json:DirectLibrary.DirectPayload|runtime-json:DirectLibrary.DirectPayload
TRANSITIVE=intercepted-call:transitive|intercepted-get:/transitive:handler|intercepted-json:TransitivePayload
MODULE_INITIALIZER=registered
```

## 実行コマンドと結果

一括検証は次のコマンドです。負例 2 件を期待どおりの失敗として扱い、最後まで成功すると終了コード 0 を返します。

```sh
cd spike/interceptors
./verify.sh
```

スクリプトが実行する主なコマンドと結果は次のとおりです。

| コマンド | 結果 |
|---|---|
| `dotnet --version` | `10.0.203` |
| `dotnet pack ...Runtime.csproj -c Release` | `Miya.Interceptors.Spike.Runtime.0.1.0.nupkg` を作成 |
| `dotnet pack ...Generator.csproj -c Release` | analyzer と props を含む `Miya.Interceptors.Spike.Generator.0.1.0.nupkg` を作成 |
| `dotnet restore TransitiveApp.csproj --configfile NuGet.config` | `DirectLibrary` を含めて成功 |
| `dotnet build DirectLibrary.csproj -c Release --no-restore` | 警告 0、エラー 0 |
| `dotnet run --project TransitiveApp.csproj -c Release --no-restore` | 直接参照と推移参照の全 assertion が成功 |
| `dotnet build DirectLibrary.csproj -t:Rebuild -p:InterceptorsNamespaces=Other.Namespace` | 想定どおり CS9137 で失敗 |
| `dotnet build ReceiverMismatch.csproj -c Release --no-restore` | 想定どおり CS9148 が 2 件 |
| `dotnet publish TransitiveApp.csproj -c Release -r osx-arm64 --self-contained true --no-restore` | IL/AOT 警告なしで成功 |
| `.../publish/TransitiveApp` | 全 assertion が成功、終了コード 0 |

runtime のスパイク用パッケージを pack したときだけ README がないという NuGet の案内が出ました。コンパイル警告と IL/AOT 警告は 0 件です。

## 落とし穴

- レシーバー式の型を使うと、継承されたメソッドで CS9148 になります。`IMethodSymbol.OriginalDefinition.ContainingType` を使う必要があります。
- `Json<T>` のレシーバーも派生 `Context` ではなく、宣言元の `Context` です。
- `InterceptorsNamespaces` が生成コードの namespace と一致しないと CS9137 になります。consumer の手動設定に任せず `buildTransitive` から追加する必要があります。
- `analyzers/dotnet/cs` への配置だけでは、`ProjectReference` の先へ generator を確実に渡せません。`buildTransitive` の props から `<Analyzer Include="..." />` も追加します。
- `GetInterceptableLocation` は call site に依存します。メソッドグループや delegate 変換には使えないため、codec 生成対象を加える `Include<T>()` が必要です。
- 位置情報はソース変更で変わります。opaque data は必ず現在の `SemanticModel` と `InvocationExpressionSyntax` から生成し、保存した値を再利用しません。
- 複数アセンブリが同じ `T` の codec を生成すると、複数の module initializer が `Registry<T>.Instance` に代入します。型ごとの設定がアプリ全体で同じなら動作は揃いますが、登録順には依存させない設計が必要です。
- 今回の推移参照は `ProjectReference` の連鎖で確認しました。中間ライブラリも NuGet パッケージにして再配布する構成では、依存パッケージの `PrivateAssets` と `developmentDependency` が変わるため、同じパッケージ行列で別に確認する必要があります。

## Miya 本実装への推奨事項

1. `c.Json<T>(value)` の通常本体は常に `Registry<T>.Instance` を使い、インターセプターを外しても同じ結果にします。
2. インターセプターは具体型 codec への薄い直接呼び出しだけに使います。生成メソッドは call site の具体型引数を持つ非ジェネリックメソッドで成立します。
3. レシーバー型は `IMethodSymbol.OriginalDefinition.ContainingType` から生成します。`App`、`App<TContext>`、派生 `Context` の見かけの型は使いません。
4. codec の生成対象は `Json<T>` の呼び出しと `MiyaJson.Include<T>()` の両方から集めます。メソッドグループとジェネリックヘルパーは後者で扱います。
5. generator パッケージは `analyzers/dotnet/cs` と `buildTransitive` を組み合わせます。props から `InterceptorsNamespaces` と `Analyzer` の両方を追加します。
6. module initializer は codec 登録だけを行い、アセンブリスキャンやリフレクションを入れません。登録が重複しても順序で挙動が変わらない契約にします。
7. パッケージ検証には直接参照、`ProjectReference` の推移参照、generator 無効、メソッドグループ、NativeAOT を残します。CS9137 と CS9148 の負例も固定します。
