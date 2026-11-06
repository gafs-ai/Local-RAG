# Local RAG ロゴ

フォルダと虫眼鏡を組み合わせたロゴ。青いフォルダと白い虫眼鏡、文字なし、背景透過PNG。

- ファイル：`local-rag-logo.png`
- 作成日：2026-11-05
- 作成方法：組み込みのimage_genツール（初回生成後、同じデザインの輪郭を整える編集）
- 原画の形と縦横比を維持し、透過余白を調整した派生ファイルを使用します。
- `local-rag.ico`：16、20、24、32、40、48、64、128、256px。実行ファイル・ウィンドウ・タスクトレイ用。
- `local-rag-128.png`：管理画面のヘッダーとプラグインのcomposerIcon用。
- `local-rag-512.png`：プラグインのlogo用。
- 派生ファイルの再生成：Windowsで `pwsh -NoProfile -File scripts/Build-BrandAssets.ps1` を実行します。
- プラグインのDesktop形式・Portable形式とも、`scripts/Package-Plugin.ps1` がPNGとアイコンの参照設定を同梱します。
- 著作権表記：GAFS Inc.。本プロジェクトのGPL-3.0-or-laterに従って配布します。

## 初回プロンプト

Use case: logo-brand. Create one polished application logo mark for a Windows desktop local document search application named Local RAG. Combine a clearly recognizable folder silhouette and a magnifying glass into one cohesive compact symbol. A bold rounded folder with a small tab, with a substantial magnifying-glass ring and short diagonal handle overlapping the folder; clean optical balance, generous negative space, very few shapes, crisp flat vector-like geometry. The design must remain recognizable as a tiny system-tray or application icon. No text, letters, slogans, decorative particles, document sheets, AI stars, 3D bevels, shadows, textures, mockup, border or surrounding tile. Render the single centered mark large in a square image with comfortable transparent margins. Truly transparent background, clean antialiased alpha edges, not a checkerboard illustration. Professional, calm, modern software identity.

## 最終編集プロンプト

Refine this logo without changing its design: preserve the blue rounded folder silhouette and the white magnifying glass, their placement, proportions, and transparent background. Make the blue and white completely flat solid fills. Remove every floating speck, stray blue or white pixel, edge debris, grain, and fuzzy artifact outside the logo. The outer contour must be geometrically smooth, crisp and clean with controlled antialiasing. There must be absolutely no isolated marks anywhere in the transparent margin. No shadows, gradients, textures, extra symbols or text. One polished production application icon, centered with transparent margins.
