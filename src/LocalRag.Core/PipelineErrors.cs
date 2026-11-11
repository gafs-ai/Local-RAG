// Copyright (C) 2026 GAFS Inc.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace LocalRag.Core;

public sealed class PipelineValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Only fixed, content-free descriptions are persisted/displayed for pipeline failures.</summary>
public static class PipelineErrors
{
    public static string DetailCode(Exception error) => error switch
    {
        PipelineValidationException validation => validation.Code,
        JsonException or KeyNotFoundException => "response_schema",
        HttpRequestException http when http.StatusCode is { } status && (int)status is >= 400 and < 500 and not (408 or 429) => "request_rejected",
        HttpRequestException => "connection",
        OperationCanceledException or TimeoutException => "timeout",
        InvalidDataException => "invalid_data",
        _ => "processing_error"
    };

    public static string Describe(string? stage, string? code) => Stage(stage) + "：" + Detail(code);

    public static string Notice(string code) => code switch
    {
        "pdf_converted" => "PDFの抽出文字をMarkdownへ変換（元ページ番号を保持）",
        "pdf_visual_content_unparsed" => "PDFの画像・図・配置の意味は未解析です。詳細は原本で確認してください",
        "pdf_headers_removed" => "PDFの繰り返しヘッダー・フッター・ページ番号を除去（機密ラベルを保持）",
        "pdf_pages_without_text" => "文字を抽出できないページがあります（画像内文字のOCRは未対応）",
        "pdf_visual" => "PDFは画像・図形主体と判定したためスキップ",
        "pdf_no_text" => "検索用の文字情報が不足していると判定したためPDFをスキップ（原本や取り込み判定の設定を確認してください）",
        "pdf_uncertain" => "PDFのサンプリングで文字主体か判断できないためスキップ（設定で文字抽出を指定できます）",
        "pdf_policy_skip" => "PDFの設定により取り込みをスキップ",
        "pdf_forced_text" => "PDFの自動判定を省略して文字を抽出",
        "pdf_repair_preserved_long" => "PDF補正の入力上限を超える段落は変換結果を保持",
        "pdf_repair_preserved_invalid" => "PDF補正の応答が不正のため変換結果を保持",
        "word_converted" => "WordをMarkdownに変換（ヘッダー・フッターは秘匿検査用に別途保持）",
        "word_heading_inferred" => "短い太字段落を見出しとして補正",
        "word_repair_preserved_long" => "Word補正の入力上限を超える段落は変換結果を保持",
        "word_repair_preserved_invalid" => "Word補正の応答が不正のため変換結果を保持",
        "word_images" => "画像は代替テキストで表現（画像内文字のOCRは未対応）",
        "word_revisions" => "変更履歴は変更後の本文を使用（削除履歴は対象外）",
        "word_comments" => "Wordのコメントは本文の対象外",
        "word_table_merged" => "結合セルのある表をテキスト化（元のセル配置は再現しません）",
        _ when code.StartsWith("encoding_auto_", StringComparison.Ordinal) => EncodingNotice(code[14..], true),
        _ when code.StartsWith("encoding_manual_", StringComparison.Ordinal) => EncodingNotice(code[16..], false),
        "format_preserved_long" => "長い段落は整形を省略して原文を保持",
        "format_preserved_invalid" => "整形応答が再試行後も不正のため原文を保持",
        "chunk_code_fallback" => "分割応答が再試行後も不正のためコードで分割",
        "category_default_conflict" => "文書の種類が混在するため既定カテゴリを使用",
        "category_default_unknown" => "分類IDが差し戻し後も不正のため既定カテゴリを使用（除外カテゴリなし）",
        "category_id_normalized" => "分類IDの表記を登録済みカテゴリへ補正",
        "code_description_structural" => "コード説明の一部は構造情報で代替（動作による検索精度に制限）",
        "code_class_description_skipped" => "クラス説明を作成できず、クラスの埋め込みを省略（関数は個別処理）",
        "code_file_description_skipped" => "ファイル説明を作成できず、ファイル単位の埋め込みを省略",
        "code_no_descriptions" => "埋め込み可能なコード説明がないため索引への公開を省略",
        "parent_embedding_skipped" => "親チャンクの埋め込みを省略（子チャンクの索引は保持）",
        "empty_text" => "検索可能な本文がありません（空文書・空白のみ）",
        _ => "代替処理あり"
    };

    public static string Status(string status, string? withdrawalReason = null, IngestionFailure? failure = null) => status switch
    {
        "ready" => "ready（検索可能）", "processing" => "processing（処理中）",
        "failed" => "failed（" + IngestionRetry.Describe(failure) + "）", "excluded" => "excluded（カテゴリによる除外）",
        "skipped" => "skipped（判定または設定による省略・本文／説明なし）",
        "deleted" => "deleted（索引対象外・原本は削除しません）" + (withdrawalReason switch
        {
            "ignored" => "：除外ルールに一致", "outside_roots" => "：監視フォルダの対象外",
            "extension_disabled" => "：拡張子の処理設定なし", "source_missing" => "：原本が見つかりません", _ => ""
        }),
        _ => status
    };

    private static string EncodingNotice(string mode, bool automatic) =>
        "文字コード：" + (TextEncodings.All.FirstOrDefault(e => e.Mode.ToString() == mode)?.Name ?? "不明") +
        (automatic ? "（自動判別）" : "（指定）");

    private static string Stage(string? stage) => stage switch
    {
        "read" => "ファイル読み取り", "prepare" => "処理準備", "classify" => "分類",
        "word_repair" => "Wordの構造補正", "pdf_repair" => "PDFの構造補正",
        "format" => "整形", "chunk" => "チャンクの結合・まとめ", "code" => "コード解析・説明作成", "embedding" => "埋め込み",
        "parent_embedding" => "親チャンクの説明・埋め込み準備", "publish" => "索引の公開", _ => "取り込み"
    };

    private static string Detail(string? code) => code switch
    {
        "pdf_invalid" => "PDFを解析できません。破損・暗号化・形式を確認してください",
        "pdf_size" => "PDFの変換上限を超えています（64MiB・2,000ページ）",
        "pdf_complexity" => "PDFの1ページの構造が複雑すぎるため保留しました",
        "pdf_extension" => "PDF用の拡張子は.pdfを指定してください",
        "word_invalid" => "Wordの構造を読み取れません。破損・暗号化やファイル形式を確認してください",
        "word_extension" => "Word用の設定には.docx・.docm・.docを指定してください",
        "word_package_size" => "Wordファイルの安全な変換上限を超えています（ファイル64MiB・展開後128MiB・XML部分16MiB）",
        "word_not_installed" => "旧形式.docの変換にはMicrosoft Wordが必要です。.docx形式への保存でも取り込めます",
        "word_conversion" => "旧形式.docをWordで変換できません。Wordの導入・ファイルの保護状態を確認するか.docxで保存してください",
        "word_conversion_timeout" => "旧形式.docの変換が2分以内に終了しませんでした。.docxで保存してください",
        "word_content_unsupported" => "埋め込み文書・数式等の未対応要素があります。通常の本文へ変換して保存してください",
        "encoding_uncertain" => "文字コードを判別できません。適用する設定で文字コードを指定してください",
        "encoding_invalid" => "指定の文字コードで読み取れないか、バイナリが含まれています。文字コードと元ファイルを確認してください",
        "encoding_bom_conflict" => "ファイルのBOMと指定した文字コードが一致しません。適用する設定を確認してください",
        "code_language" => "適用する設定で対応する言語を選択してください",
        "code_parser" => "構文解析ライブラリを実行できません。解析環境の取得・検証と設定した言語を確認してください",
        "code_syntax" => "ソースコードの構文を解析できません。修正またはテキスト処理への切り替えが必要です",
        "code_units" => "関数・クラスの数が上限を超えています",
        "chunk_order" => "段落範囲に欠落・重複・順序の誤りがあります",
        "chunk_end" => "存在しない、または開始位置より前の終端段落が指定されました",
        "chunk_size" => "チャンクが文字数上限を超えています",
        "chunk_coverage" => "未分割の段落があります",
        "response_schema" => "モデル応答の形式が不正です",
        "category_unknown" => "未定義の分類IDが指定されました",
        "source_changed" => "処理中に元ファイルが変更されました",
        "category_conflict" => "区間ごとの分類が食い違っています。カテゴリを絞るか分類を省略して指定してください",
        "context_configuration" => "指示文またはカテゴリ一覧が長すぎます。プロンプトを短くするか有効カテゴリを絞ってください",
        "context_capacity" => "指示文と応答の予約を除くと本文を処理する余地がありません",
        "format_ids" => "除去対象に存在しない段落IDが指定されました",
        "format_destructive" => "整形による削除量が大きいため保留しました",
        "decision_invalid" => "機密検査の判定値が不正です",
        "chat_context" => "モデルの入力上限を超える可能性があります",
        "embedding_context" => "Embeddingモデルの入力上限を超える可能性があります。短いチャンクへの分割が必要です",
        "output_incomplete" => "モデル出力が正常に完了しませんでした",
        "connection" => "接続に失敗しました。接続先の稼働状態を確認してください",
        "request_rejected" => "接続先がリクエストを拒否しました。接続・モデル・認証の設定を確認してください",
        "timeout" => "応答待ちがタイムアウトしました",
        "invalid_data" => "入力または応答が検証を通過しませんでした",
        "document_size" or "file_size" => "文書サイズが上限を超えています。パイプライン設定の文書文字数上限を増やしてください",
        "empty_text" => "検索可能な本文がありません（空文書・空白のみ）",
        "binary_text" => "テキストにNULが含まれています。文字コードと元ファイルを確認してください",
        _ => "処理に失敗しました"
    };
}
