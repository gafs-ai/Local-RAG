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
        "format_preserved_long" => "長い段落は整形を省略して原文を保持",
        "format_preserved_invalid" => "整形応答が再試行後も不正のため原文を保持",
        "chunk_code_fallback" => "分割応答が再試行後も不正のためコードで分割",
        _ => "代替処理あり"
    };

    private static string Stage(string? stage) => stage switch
    {
        "read" => "ファイル読み取り", "prepare" => "処理準備", "classify" => "分類",
        "format" => "整形", "chunk" => "チャンク分割", "embedding" => "埋め込み",
        "publish" => "索引の公開", _ => "取り込み"
    };

    private static string Detail(string? code) => code switch
    {
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
        _ => "処理に失敗しました"
    };
}
