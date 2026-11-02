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
        "format_ids" => "除去対象に存在しない段落IDが指定されました",
        "format_destructive" => "整形による削除量が大きいため保留しました",
        "decision_invalid" => "機密検査の判定値が不正です",
        "chat_context" => "モデルの入力上限を超える可能性があります",
        "output_incomplete" => "モデル出力が正常に完了しませんでした",
        "connection" => "接続に失敗しました。接続先の稼働状態を確認してください",
        "timeout" => "応答待ちがタイムアウトしました",
        "invalid_data" => "入力または応答が検証を通過しませんでした",
        _ => "処理に失敗しました"
    };
}
