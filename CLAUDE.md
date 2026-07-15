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

# DDS 抽出/書き戻し (単体実行。通常は --ps3 の unpack/pack が自動で行うので不要)
SegaRallyRevoTool exportdds -i <unpacked_dir>
SegaRallyRevoTool importdds -i <unpacked_dir>
```

### PS3 テクスチャ編集フロー (2026-07-15 実装)

`--ps3` 指定時は DDS 変換が自動で行われる:

1. `unpack -i xxx.vbf --ps3` — 4/ への生スライス分割に加え、**標準 DDS を `dds/` へ自動抽出**
   (BE ヘッダーを LE 化。通常のテクスチャエディタで開ける)
2. `dds/{i:D8}.DDS` を編集する (**サイズ変更も可**。4/ や _meta は触らない)
3. `pack -i <unpacked_dir> --ps3` — **`dds/` の内容を 4/ へ自動で書き戻してから**パック
   (ヘッダー BE 化、ExtraHeader +0x00 の論理オフセット再計算、アラインメント維持)

`dds/` が存在しない場合 (type5 のみ等) は書き戻しをスキップ。書き戻し検証に失敗した場合は
パックを中止する。

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

**PS3 テクスチャの詳細レイアウト (2026-07-12〜15 検証で解明)**:

各 type4 テクスチャの実体は次の物理構造で格納されている:

```
[パディング/前置領域]
[BE DDS ヘッダー 0x80]   ← dword (4バイト) 単位でバイト反転されたビッグエンディアン形式
                            (マジックは ` SDD`、dwSize=124 は `00 00 00 7C`)
[ペイロード]              ← 0x1000 アラインで開始。バイトスワップ無し。
                            **PC 版 DDS のペイロード (ピクセル+ミップ全長) と完全同一**
[0x44 バイトの領域]       ← 意味未解明 (実測ではゼロ埋め)。次テクスチャのヘッダーが続く
```

- BE ヘッダーを dword 反転して LE 化すると PC 版とバイト一致する正しい DDS ヘッダーになる
  (loading_alpine1 ではコンテナ 0〜2 の抽出 DDS が PC 版とヘッダー含め完全一致)
- 最終テクスチャはファイル終端で切られており、PC 版よりわずかに短いことがある
  (実測 8 バイト。EOF は必ずしも 0x1000 アラインではない)
- **ExtraHeader +0x00 は物理オフセットではなく論理値**:
  `extra[i+1] - extra[i] = 0x80 + payload[i] 長 (= PC 版 DDS ファイルサイズ)` の累積和。
  基点 extra[0] = 0x1F68 (loading_alpine1 / livery19 で同値)。テクスチャの物理位置は
  この論理値とは一致しない (ペイロードは物理的には次コンテナの論理スライスにはみ出す)
- `headersEnd` から最初の BE ヘッダーまでの前置領域は全ゼロだった

Unpacker はこの論理値を境界に生スライスを 4/ へ保存し (バイト一致保証)、
DDS 抽出/書き戻し (`dds/` フォルダ) はスライスを跨いでペイロードを再構成する。
詳細は「PS3 テクスチャ編集フロー」と DdsExporter.cs / DdsImporter.cs を参照。

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
    dds/                    ← PS3 (--ps3) のみ: 自動抽出された標準 DDS (編集用)
      00000000.DDS             pack --ps3 時に 4/ へ自動で書き戻される
      ...
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
  DdsExporter.cs                  ← PS3 生スライス → 標準 DDS 抽出
  DdsImporter.cs                  ← 標準 DDS → PS3 生スライス書き戻し (サイズ変更対応)
  SegaRallyRevoLib/
    SBF.cs                        ← Entry / Container クラス
    SBZ1.cs                       ← SBZ1 圧縮フォーマット
    Ps3DdsUtil.cs                 ← BE DDS ヘッダー検出・dword 反転ヘルパー
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
  (PC サンプルでは実際に `.DDS` になる。PS3 の生スライスは BE ヘッダーが埋め込み位置に
  あるため `.BIN` になる — 標準 DDS は dds/ に自動抽出される)

以下は DDS 抽出/書き戻し機能の検証結果 (2026-07-15):

- 自動フロー (unpack --ps3 → dds/ 自動抽出 → pack --ps3 で自動書き戻し) が無変更なら
  元 VBF とバイト一致 (loading_alpine1 / livery19 / livery36 の 3 ファイル)
- 抽出された dds/*.DDS は PC 版の対応 DDS とバイト完全一致 (loading_alpine1 のコンテナ 0〜2。
  コンテナ 3 = 最終テクスチャのみ EOF 切り詰めにより 8 バイト短い)
- サイズ変更の自己整合: テクスチャ 0 (36796B) を別サイズの DDS (135100B) に差し替えて
  pack → 再 unpack した結果、差し替えた DDS が元通り復元され、他の 3 テクスチャも無傷。
  ExtraHeader +0x00 は論理オフセット規則どおり再計算された
- type5 のみのアーカイブでは dds/ は生成されず、既存ラウンドトリップに影響なし。
  PC 経路もリグレッションなし
- **実機 (PS3) での動作確認は未実施** (サイズ変更した VBF をゲームが受け付けるかは未検証)

## 既知の未解決事項

- テクスチャ間の 0x44 バイト領域の意味は未解明 (実測ではゼロ埋め。書き戻し時は不透明
  バイトとして再利用している)。また extra[0] 基点 0x1F68 が全ファイル固定かどうかは
  サンプル不足で断定できていない (実装はアンパック時の値を据え置く安全側)
- サイズ変更した VBF を実機 (PS3) が受け付けるかは未検証 (自己整合性のみ確認済み)
- PS3 (VBF) で type4 と type5 が同じアーカイブ内に混在し、かつ type5 側にも
  ExtraHeader 相当のデータ位置情報が必要になるケースは未検証・未対応
  (今回のサンプルはいずれも「type4 のみ」または「type5 のみ」だった)。
  混在時は type4 分割ロジックの対象外となり、type5 コンテナにはデータファイルが
  割り当てられない可能性がある
- type==5 Container のデータ構造(子 Container の unk0x04 ハッシュリスト)はリバース済みだが Pack 側未実装
