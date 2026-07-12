# SegaRallyRevoTool — CLAUDE.md

## プロジェクト概要

Sega Rally Revo (PC / PS3) のアーカイブファイル (SBF / VBF) を
アンパック・パックするコマンドラインツール。

- **PC版**: SBF (リトルエンディアン)、圧縮時は SBZ1 (ZLib ラッパー)
- **PS3版**: VBF (ビッグエンディアン)

---

## ビルド

```bash
dotnet build SegaRallyRevoTool/SegaRallyRevoTool.csproj
dotnet run --project SegaRallyRevoTool/SegaRallyRevoTool.csproj -- --help
```

ターゲットフレームワーク: **net5.0**
主要 NuGet パッケージ:
- `AuroraLib.Compression 2.0.0` — ZLib 圧縮/伸長
- `CommandLineParser 2.9.1` — CLI 引数パース

---

## 使い方

```bash
# アンパック (PC/SBF)
SegaRallyRevoTool unpack -i <input.sbf> [-o <output_dir>]

# アンパック (PS3/VBF)
SegaRallyRevoTool unpack -i <input.vbf> --ps3 [-o <output_dir>]

# 解凍のみ (SBZ1 → SBF)
SegaRallyRevoTool unpack -i <input.sbf> --decomp

# パック (PC)
SegaRallyRevoTool pack -i <unpacked_dir> [-o <output_dir>]

# パック (PS3)
SegaRallyRevoTool pack -i <unpacked_dir> --ps3 [-o <output_dir>]

# パック・圧縮なし
SegaRallyRevoTool pack -i <unpacked_dir> --nocomp
```

---

## ファイルフォーマットのメモ

### SBF / VBF 全体レイアウト

```
+0x00  DWORD  type4エントリー数 (type5 を除いた数)
+0x04  ...    padding (zeros)
+0x14  DWORD  全 Container 数
+0x18  Entry[] (8 bytes × count)
       各 Entry: unk0x00(4) + offset(4)
[各 offset 位置]
       Container ヘッダー (0x1C)
       ExtraHeader (0x28) ※ type==4 のみ
       データ本体 (DDS など)
```

### エンディアン

| ファイル | エンディアン | `--ps3` フラグ |
|---------|------------|---------------|
| SBF (PC) | リトルエンディアン | 不要 |
| VBF (PS3) | ビッグエンディアン | 必要 |

### Container type

| type | 意味 | ExtraHeader |
|------|------|-------------|
| `0x04` | テクスチャファイル (DDS) | あり (0x28 bytes) |
| `0x05` | グループ/インデックス | なし |

### ExtraHeader (0x28 bytes, type==4 のみ)

| オフセット | SBF (PC) | VBF (PS3) | 意味 |
|-----------|---------|---------|------|
| +0x00 | `0x00000000` (常にゼロ) | DDSデータの絶対オフセット | レイアウト差吸収フィールド |
| +0x04 | テクスチャ幅 | テクスチャ幅 | 例: 0x100 = 256px |
| +0x08 | テクスチャ高さ | テクスチャ高さ | 例: 0x080 = 128px |
| +0x0C〜+0x24 | 固定値 | 固定値 | 両プラットフォームで同一 |

### SBF データ配置 (PC)

```
ContainerHeader(0x1C) + ExtraHeader(0x28) + DDSデータ
↓ padding
ContainerHeader(0x1C) + ExtraHeader(0x28) + DDSデータ ...
```

パディング規則: `roundUp(0x7C + linearSize, 0x1000)` で次 Container の先頭を揃える

### VBF データ配置 (PS3)

```
ContainerHeader(0x1C) + ExtraHeader(0x28)  ← 全ヘッダー連続 (headersEnd)
ContainerHeader(0x1C) + ExtraHeader(0x28)
...
[未知領域 _predata]                        ← headersEnd 〜 最初の DDS オフセットまでの隙間
DDSデータ[0] (+ 末尾に未知領域)
DDSデータ[1] (+ 末尾に未知領域) ...
DDSデータ[n]                                ← 末尾は EOF まで (末尾にも未知領域を含む)
```

DDSデータの位置は ExtraHeader +0x00 の絶対オフセットで参照する。

**未知領域について (2026-07-12 調査結果)**: `headersEnd` から最初の DDS オフセットまでの隙間
(例: loading_alpine1_data.vbf では `0x1110`〜`0x1F68` = 0xE58 バイト) は全ゼロだった。
一方、各 DDS データの直後〜次の DDS オフセットまでの隙間 (同ファイルでは各コンテナ後ろに
固定 0xF10 バイト、livery19/livery36 系ファイルでは 0x458 バイト) は非ゼロで、
規則的なバイトパターンの繰り返しが見られた (ミップマップの残骸か GPU 側のアラインメント/
確保単位に由来するパディングと推測されるが、正体は未確定)。この隙間サイズは同一ファイル内では
一定だが、ファイルによって異なる値になる。
用途を厳密に解明せずともバイト一致を保つため、実装ではこれらの隙間を意味解釈せずに
「直前 or 直後のデータの一部」としてそのまま保存し、Pack 時にファイルサイズから
オフセットを逆算する設計にした (詳細は「アンパック出力ディレクトリ構成」を参照)。

---

## アンパック出力ディレクトリ構成

```
<output_dir>/
  <filename>/
    4/                      ← type4 Container のデータ (DDS / BIN)
      00000000.DDS          (DDS マジックがあれば .DDS、なければ .BIN)
      00000001.DDS
      ...
    5/                      ← type5 Container のデータ (存在すれば)
    _meta/
      _header.bin           ← SBF グローバルヘッダー
      _order.txt            ← パック時の Container 順序
      _predata.bin           ← PS3 (VBF) のみ: ヘッダー連続部の直後〜最初の DDS データ
                                オフセットまでの未知領域 (生バイトのまま保存)
      00000000.HEAD         ← ContainerHeader (0x1C, 常に固定サイズ)
      00000001.HEAD
      00000001.EXTRA        ← ExtraHeader (0x28, type==4 のみ存在)
      00000002.HEAD
      00000002.EXTRA
      ...
```

`.EXTRA` ファイルが存在するかどうかで type==4 か否かを判別する。
Packer は `.EXTRA` の有無を `File.Exists()` で確認して挙動を切り替える。

### PS3 (VBF) の type4 分割アンパック / Pack 時のオフセット逆算

PC (SBF) と異なり PS3 (VBF) は ContainerHeader/ExtraHeader が全コンテナ分連続しているため、
Unpacker は type4 コンテナについて ExtraHeader+0x00 (DDS絶対オフセット, .EXTRA 内では常に LE 保存)
を境界として使い、`4/{index:D8}.{ext}` に 1 コンテナ 1 ファイルで分割抽出する。

- コンテナ `i` のデータ範囲: `[ExtraHeader[i].+0x00, ExtraHeader[i+1].+0x00)` (最後のコンテナは EOF まで)
  → 前述の「未知領域」はこの範囲にそのまま含まれる (直前のデータの一部として保存される)
- ヘッダー連続部の直後〜最初の DDS オフセットまでの隙間は `_meta\_predata.bin` に保存
- type4 コンテナが 1 つも無い場合 (type5 のみの `.sbf`/`.vbf` など) は分割を行わず、
  従来どおり Entry オフセットベースで最後の Container に残りデータを丸ごと割り当てる
  raw 方式にフォールバックする (`_predata.bin` は出力されない)

Packer 側 (`ps3SplitMode` = `_isBigEndian` かつ `_meta\_predata.bin` が存在する場合) は:

1. 全 `.HEAD`/`.EXTRA` の有無からヘッダー連続部の合計サイズを求める
2. `_header.bin` の長さ + ヘッダー連続部サイズ + `_predata.bin` の長さ を起点に、
   `_order.txt` (`dataFileByIndex`) から得た type4 データファイルのサイズを順に積算して
   各コンテナの絶対オフセットを再計算する
3. `.EXTRA` の先頭 4 バイト (LE) をこの再計算値で上書きしてから ContainerHeader/ExtraHeader を書き込み
4. ヘッダーを全コンテナ分書き終えた後に `_predata.bin` → 各 type4 データファイルを順に書き込む

この設計により、テクスチャサイズを変更するような改造 (mod) を行っても、後続コンテナの
ExtraHeader オフセットは Pack 時に自動的に再計算される。

---

## ソースファイル構成

```
SegaRallyRevoTool.sln
SegaRallyRevoTool/
  Program.cs                      ← CLI エントリポイント・Verb 定義
  Unpacker.cs                     ← アンパック処理
  Packer.cs                       ← パック処理
  SegaRallyRevoLib/
    SBF.cs                        ← Entry / Container クラス
    SBZ1.cs                       ← SBZ1 圧縮フォーマット
```

---

## テスト状況 (2026-07-12 時点)

以下はすべて `format/archive/` のサンプルでバイト一致のラウンドトリップを確認済み:

- PC SBF: unpack → pack --nocomp → 元データと一致 (LE_PC_loading_alpine1_data.sbf ほか)
- PS3 VBF (非圧縮, type4 分割抽出): unpack --ps3 → pack --ps3 --nocomp → 元ファイルと一致
  (loading_alpine1_data.vbf: type4 Container 4 つ、livery19_data.vbf: type4 Container 2 つ)
- PS3 VBF (SBZ1 圧縮, livery36_data.vbf): unpack --ps3 --decomp で得た生データと
  unpack --ps3 → pack --ps3 --nocomp の出力がバイト一致
- PS3_ver フォルダの `.sbf` (loading_alpine1_data.sbf / livery19_data.sbf / livery36_data.sbf) は
  拡張子に反して**ビッグエンディアン**なので `--ps3` が必要。いずれも type4 を含まず
  type5 のみの小さいアーカイブ (raw フォールバック方式) で、`--decomp` 後の生データと
  unpack→pack --nocomp の出力が一致することを確認済み
- type4 分割アンパック時、4/ 配下に type4 Container 数と同数のデータファイルが生成され、
  DDS マジック (`"DDS "`) を持つデータは `.DDS` 拡張子になることを確認済み
  (PC サンプルでは実際に `.DDS` になる。今回検証した PS3 VBF サンプルのテクスチャデータは
  いずれも `"DDS "` マジックを持たない独自形式のため `.BIN` になる — これは想定内の挙動)

## 既知の未解決事項

- PS3 (VBF) の type4 分割アンパック + Pack 時の ExtraHeader +0x00 逆算は実装済みだが、
  各 DDS データの前後にある「未知領域」(全ゼロではない固定長の隙間。正体はミップマップの
  残骸か GPU 側のアラインメント/確保単位に由来するパディングと推測されるが未確定) の
  意味は解明できていない。現在の実装ではこの領域を意味解釈せず「直前のコンテナデータの
  末尾に含める」形でバイト単位保存しているため、ラウンドトリップは成立するが、
  仮にこの領域が本当にテクスチャデータ(ミップマップ等)の一部だった場合、それを含めて
  4/ 以下のデータファイルとして抽出されている点に留意すること
- PS3 (VBF) で type4 と type5 が同じアーカイブ内に混在し、かつ type5 側にも
  ExtraHeader 相当のデータ位置情報が必要になるケースは未検証・未対応
  (今回のサンプルはいずれも「type4 のみ」または「type5 のみ」だった)。
  混在時は type4 分割ロジックの対象外となり、type5 コンテナにはデータファイルが
  割り当てられない可能性がある
- type==5 Container のデータ構造(子 Container の unk0x04 ハッシュリスト)はリバース済みだが Pack 側未実装
