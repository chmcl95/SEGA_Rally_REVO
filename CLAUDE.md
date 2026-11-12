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
2. `dds/{i:D8}.DDS` を編集する (**サイズ・寸法の変更も可**。4/ や _meta は触らない)
3. `pack -i <unpacked_dir> --ps3` — **`dds/` の内容を 4/ へ自動で書き戻してから**パック

書き戻し時に更新される値:

| 対象 | 値 |
|------|-----|
| ExtraHeader +0x00 | 論理オフセット (累積再計算) |
| ExtraHeader +0x04 / +0x08 | 幅 / 高さ (差し替え後の DDS ヘッダーから) |
| ContainerHeader +0x08 | size = ミップ全段の合計 + 0xAC |

`dds/` が存在しない場合 (type5 のみ等) は書き戻しをスキップ。書き戻し検証に失敗した場合は
パックを中止する。書き出しは全コンテナぶんを組み立ててから一括で行うため、
エラー時に作業ディレクトリが中途半端な状態になることはない。

抽出される `dds/{i:D8}.DDS` は `0x80 + size - 0xAC` バイトちょうど
(パディングを含まない正確な DDS) になる。

---

## ファイルフォーマットのメモ

> **正本は [`Document/SBF_format.md`](Document/SBF_format.md) (2026-11-12 再解析、製品版全 2761 アーカイブで検証)。**
> 検証スクリプトは `tools/sbf_inspect.py` (`py -3 -I tools/sbf_inspect.py --validate <dir>`)。
> 以下は旧ツール実装時のメモで、**ツールの入出力 (`.HEAD` / `.EXTRA`) の説明として残している**。
> 構造の解釈は再解析で次のように訂正された:
>
> - ブロックヘッダーは固定 0x1C ではない。
>   `type, hash, size, sub, nReloc, reloc[], nImport, import[], data[size]` の可変長
>   (内部ポインタ再配置表と、外部参照 = 他リソースのハッシュの表を持つ)。
>   type 4 は nReloc = nImport = 0 なので、たまたま「0x1C + 0x28」と同じ 0x44 バイトになる
> - 「ExtraHeader」はテクスチャ記述子 (0x2C) の一部。+0x0C〜+0x24 の「固定値」は
>   アドレスモード U/V/W・フィルタ・LOD バイアス・最大異方性
> - type は 1, 2, 3, 4, 5, 6, 7, 11, 12, 13 の 10 種類
>   (Model / Material / Shader / Texture / DataTable / Particle / RawFile / SceneGraph / TypedObject / TextureList)
> - PS3 の `.sbf` は type 4 以外、`.vbf` は type 4 のみ。両方を合わせると PC の `.sbf` と同じリソース群になる
> - グローバル +0x00 はビルドバージョン (v3 / v4)。+0x04 は VBF の分割継続フラグ、+0x08 は VBF のメインメモリ部の長さ
> - VBF の 0x44 gap は、先頭へ移したヘッダー + 記述子の空き席
> - PC のパディング規則は「type 4 の DDS ペイロード (ブロック先頭 + 0xC4) を 0x1000 境界へ」。
>   それ以外の type は、データ先頭を type ごとの境界に揃える。v4 の EOF は `roundUp(end, 4) + 0x800`
> - PS3 インストール先の `livery55_data.vbf` (2026-07-15 に旧ツールで再パック) は
>   テクスチャ 1 のデータポインタが誤っている (0xACAA0、正しくは 0xACF24)

### SBF / VBF 全体レイアウト

```
+0x00  DWORD  用途未解明 (値は 3 か 4 のみ。フォーマットのバージョンか)
+0x04  DWORD  用途未解明 (0 か 1 のフラグ)
+0x08  DWORD  VBF のみ: ヘッダー連続部の終端オフセット。SBF では 0
+0x0C  ...    padding (zeros)
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

### ContainerHeader (0x1C bytes)

| オフセット | 意味 |
|-----------|------|
| +0x00 | type |
| +0x04 | ハッシュ (Entry の unk0x00 と同じ値) |
| +0x08 | **size** |
| +0x0C〜+0x18 | 未解明 |

type==4 の `size` は次式で求まる (PC/PS3 のサンプル全 358 テクスチャで成立を確認):

```
size = DDS ヘッダーが宣言するミップ全段の合計バイト数 + 0xAC
```

テクスチャを差し替えたときはこの値の再計算が必要。`DdsImporter` が自動で行う。

### Container type

| type | 意味 | ExtraHeader |
|------|------|-------------|
| `0x01` `0x02` `0x03` | 未解明 (モデル/マテリアル系と推測) | なし |
| `0x04` | テクスチャファイル (DDS) | あり (0x28 bytes) |
| `0x05` | グループ/インデックス | なし |
| `0x0C` `0x0D` | 未解明 | なし |

type 1/2/3/12/13 は不透明バイト列として保存・復元するのみ (round-trip は成立する)。

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

**PS3 テクスチャの詳細レイアウト (2026-07-31 に全サンプルで再検証・確定)**:

データ領域 (絶対オフセット `extra[0]` 以降) の物理構造は次のとおり。
サンプル全 37 アーカイブ / 183 テクスチャで規則の成立を確認済み。

```
[先頭パディング 0x1018]   ← extra[0] から最初の BE ヘッダーまで。全ゼロ
[BE DDS ヘッダー 0x80]    ← dword (4バイト) 単位でバイト反転されたビッグエンディアン形式
                             (マジックは ` SDD`、dwSize=124 は `00 00 00 7C`)
[ペイロード]               ← 0x1000 アラインで開始。バイトスワップ無し。
                             長さ = ContainerHeader +0x08 (size) - 0xAC
[0x44 バイトの gap]        ← 意味未解明 (実測では常にゼロ)
[アラインメント埋め]        ← 次のペイロード開始を 0x1000 アラインに揃える。実測では常にゼロ
[BE DDS ヘッダー 0x80] ...
```

- テクスチャ *k* の物理間隔は `roundUp(payloadLen + 0xC4, 0x1000)` で決まる
- **最終テクスチャはペイロード直後が EOF** (gap もアラインメント埋めも無い)。
  EOF は必ずしも 0x1000 アラインではない
- BE ヘッダーを dword 反転して LE 化すると PC 版と同じ正しい DDS ヘッダーになる
- **ExtraHeader +0x00 は物理オフセットではなく論理値**:
  物理レイアウトからテクスチャあたり gap の 0x44 バイトを差し引いた別の区切り方であり、
  `extra[k+1] - extra[k] = 物理間隔 - 0x44 = 0x80 + payloadLen + アラインメント埋め長`。
  基点 `extra[0] = 0x1F68` は全 37 アーカイブで同値

> **重要**: 論理スライスと物理配置はテクスチャごとに 0x44 ずつずれていくため、
> **「論理スライス *k* がテクスチャ *k* の BE ヘッダーを含む」とは限らない**。
> 論理サイズが `0x1018 + 0x80` より小さいテクスチャが含まれると対応が 1 つずれる
> (2026-07-31 に修正。それ以前の実装はスライス内をヘッダー検索していたため、
> 13 アーカイブで誤ったテクスチャを抽出していた)。

Unpacker は論理値を境界に生スライスを 4/ へ保存し (バイト一致保証)、
DDS 抽出/書き戻し (`dds/` フォルダ) は**生スライスを index 順に連結して物理バイト列を
復元し、BE ヘッダーを出現順に type4 コンテナへ対応付ける**。
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
    Ps3DdsUtil.cs                 ← BE DDS ヘッダー検出・dword 反転ヘルパー・レイアウト定数
    DdsHeader.cs                  ← LE DDS ヘッダー解釈 (幅/高さ/ミップ合計サイズ算出)
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

## テスト状況 (2026-07-31 全サンプルで再検証)

対象は `z_claude/sample` 配下の `.sbf`/`.vbf` **全 107 ファイル**
(PC/PS3、SBZ1 圧縮あり・なし、type4 のみ / type5 のみ / 混在、`.1.vbf`〜`.4.vbf` の
分割アーカイブ、空アーカイブを含む)。

- **ラウンドトリップ: 99 / 107 がバイト一致** (`unpack` → `pack --nocomp`、
  `--ps3` の DDS 自動抽出・自動書き戻しを含む標準フロー)
  - 残り 8 件はコンテナ数 0 の空アーカイブ (24 バイト) で、`unpack` が
    「Invalid SBF file.」として弾く。**未対応の既知課題**
- **DDS 対応付け: 37 アーカイブ / 183 テクスチャすべてで正しい**
  (抽出された `dds/{i}.DDS` の寸法が ExtraHeader[i] と一致し、
  長さが `0x80 + size - 0xAC` と一致することを確認)
- **サイズ・寸法変更 mod の整合性**: loading_alpine1 のテクスチャ 0 (256×128 DXT5) を
  256×1024 DXT1 に差し替えて pack した結果、
  size / 幅 / 高さ / 後続の論理オフセット / 0x1000 アラインメント / EOF 規則が
  すべて規則どおりに更新され、再 unpack で差し替えたテクスチャが復元し他は無傷
- PC 経路・type5 のみのアーカイブともリグレッションなし
- **実機 (PS3) での動作確認は未実施**

## 既知の未解決事項

- テクスチャ間の 0x44 バイト gap の意味は未解明 (実測では常にゼロ。書き戻し時は
  不透明バイトとして再利用している)
- サイズ変更した VBF を実機 (PS3) が受け付けるかは未検証
- **コンテナ数 0 の空アーカイブ (24 バイト) を unpack できない** (8 サンプルが該当)
- エンディアンが手動指定 (`--ps3`) で、取り違えると未処理例外で落ちる。
  `PS3_ver` の `.sbf` は拡張子に反して BE なので注意
- PC (SBF) 経路はテクスチャ差し替え未対応 (アラインメント再計算も size 再計算も無い)
- グローバルヘッダー +0x00 の意味 (値は 3 か 4。type4 の個数とは無関係)、
  +0x04 のフラグ、type 1/2/3/12/13 の構造は未解明
- PS3 (VBF) で type4 と type5 が同じアーカイブ内に混在し、かつ type5 側にも
  ExtraHeader 相当のデータ位置情報が必要になるケースは未検証・未対応
  (今回のサンプルはいずれも「type4 のみ」または「type5 のみ」だった)。
  混在時は type4 分割ロジックの対象外となり、type5 コンテナにはデータファイルが
  割り当てられない可能性がある
- type==5 Container のデータ構造(子 Container の unk0x04 ハッシュリスト)はリバース済みだが Pack 側未実装
