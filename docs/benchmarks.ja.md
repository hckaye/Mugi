# ベンチマーク

[English](benchmarks.md) | [日本語](benchmarks.ja.md)

計測は Apple M5 CPU（物理 10 コア）、macOS Tahoe 26.5.2、.NET SDK 10.0.203、.NET ランタイム 10.0.7 arm64 で行いました。BenchmarkDotNet 0.15.8 は concurrent workstation GC を使いました。

## NativeAOT サンプル

`dotnet publish samples/Hello/Hello.csproj -c Release` は IL や AOT の警告なしで完了しました。publish した実行ファイルは `dotnet` なしで動き、Text、JSON、404、405、HEAD のリクエストを通しました。

| 項目 | 結果 |
| --- | ---: |
| `Hello` 実行ファイルのサイズ | 7,128,392 bytes (6.80 MiB) |
| プロセス起動から `GET /` 完了まで、10 回の中央値 | 8.443 ms |

起動のサンプルは 21.598、9.278、8.435、8.452、8.848、8.710、7.641、8.389、7.446、8.114 ms でした。各回は新しい loopback ポートで新しい NativeAOT プロセスを起動し、HTTP レスポンスを受け取り終えてから停止しました。

## ASP.NET Core とのメモリ・スループット比較

`benchmarks/Miya.LoadBench` は、Miya サーバーと等価な ASP.NET Core Minimal API サーバーを 1 つずつ起動し、loopback の HTTP/1.1 で `HttpClient` の負荷クライアント（128 並行、3 秒のウォームアップ、10 秒の計測）でそれぞれに負荷をかけます。両サーバーの `GET /`、`GET /users/123`、`POST /echo` は同じレスポンス本文を返します。ASP.NET Core サーバーは `WebApplication.CreateSlimBuilder`、Minimal API のルーティング、camelCase を指定した System.Text.Json の source generation を使い、コンソールログは無効、アプリケーション固有のサービスは未登録です。

スループットはどのエンドポイントでも両者の差がおよそ 1〜3% で、共通の Kestrel 転送層と整合します。Miya はピーク作業セットが約 20 MiB 小さく、リクエストあたりの割り当ても少ない結果でした。表は 3 反復の中央値です。

| エンドポイント | フレームワーク | スループット (req/s) | p99 | ピーク作業セット | 1 リクエストあたりの割り当て |
| --- | --- | ---: | ---: | ---: | ---: |
| plaintext | Miya | 176,258 | 1.971 ms | 76.8 MiB | 104 B |
| plaintext | ASP.NET Core | 174,897 | 1.960 ms | 97.8 MiB | 1,016 B |
| JSON GET | Miya | 174,233 | 1.945 ms | 78.7 MiB | 168 B |
| JSON GET | ASP.NET Core | 175,505 | 1.967 ms | 98.4 MiB | 384 B |
| JSON POST | Miya | 171,429 | 1.968 ms | 78.0 MiB | 400 B |
| JSON POST | ASP.NET Core | 166,704 | 2.048 ms | 100.5 MiB | 648 B |

リクエストあたりの割り当ては、サーバーの `GC.GetTotalAllocatedBytes` の計測区間での差分をリクエスト数で割った値です。ピーク作業セットは、サーバーの実行中に 10 ms 間隔で取得した `WorkingSet64` の最大値です。

同じ JIT 比較は次のコマンドで実行できます。

```sh
./benchmarks/Miya.LoadBench/run.sh \
  --concurrency 128 --warmup 3 --duration 10 --iterations 3
```

## Miya と System.Text.Json

シリアライザーのジョブは、1 回の launch、5 回の warmup、20 回の計測、250 ms の iteration time を使いました。Miya は `Miya.Generators` が生成した codec を使い、codec を渡さない `Json.Serialize` と `Json.Deserialize` のオーバーロードで解決しました。ベンチマーク専用の codec は使っていません。

両方のシリアライザーは再利用した `IBufferWriter<byte>` に書き込みました。System.Text.Json は source generation、camelCase 命名、`UnsafeRelaxedJsonEscaping`、required メンバー検査、nullable 注釈検査を使いました。リクエスト JSON は計測区間の前に用意し、両シリアライザーが required プロパティの欠落と非 nullable プロパティへの null をどちらも拒否することを setup で確認しました。バッファ拡張のケースは各操作の中で 16 バイトのバッファを作りました。

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

## ルーティングとミドルウェアパイプライン

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

## ベンチマークの実行

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
