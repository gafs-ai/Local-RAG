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
        HttpRequestException => "connection",
        OperationCanceledException or TimeoutException => "timeout",
        InvalidDataException => "invalid_data",
        _ => "processing_error"
    };

    public static string Describe(string? stage, string? code) => Stage(stage) + "：" + Detail(code);

    public static string Notice(string code) => code switch
    {
        _ when code.StartsWith("encoding_auto_", StringComparison.Ordinal) => EncodingNotice(code[14..], true),
        _ when code.StartsWith("encoding_manual_", StringComparison.Ordinal) => EncodingNotice(code[16..], false),
        "format_preserved_long" => "長い段落は整形を省略して原文を保持",
        "format_preserved_invalid" => "整形応答が再試行後も不正のため原文を保持",
        "chunk_code_fallback" => "分割応答が再試行後も不正のためコードで分割",
        "category_default_conflict" => "文書の種類が混在するため既定カテゴリを使用",
        "code_description_structural" => "コード説明の一部は構造情報で代替（動作による検索精度に制限）",
        "empty_text" => "検索可能な本文がありません（空文書・空白のみ）",
        _ => "代替処理あり"
    };

    public static string Status(string status, string? withdrawalReason = null) => status switch
    {
        "ready" => "ready（検索可能）", "processing" => "processing（処理中）",
        "failed" => "failed（処理保留・再試行対象）", "excluded" => "excluded（カテゴリによる除外）",
        "skipped" => "skipped（本文なし・更新時に再確認）",
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
        "format" => "整形", "chunk" => "チャンクの結合・まとめ", "code" => "コード解析・説明作成", "embedding" => "埋め込み",
        "publish" => "索引の公開", _ => "取り込み"
    };

    private static string Detail(string? code) => code switch
    {
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
        "timeout" => "応答待ちがタイムアウトしました",
        "invalid_data" => "入力または応答が検証を通過しませんでした",
        "document_size" or "file_size" => "文書サイズが上限を超えています。パイプライン設定の文書文字数上限を増やしてください",
        "empty_text" => "検索可能な本文がありません（空文書・空白のみ）",
        "binary_text" => "テキストにNULが含まれています。文字コードと元ファイルを確認してください",
        _ => "処理に失敗しました"
    };
}
