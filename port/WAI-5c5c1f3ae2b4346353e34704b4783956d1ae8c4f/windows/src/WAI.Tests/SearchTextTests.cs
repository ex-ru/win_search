using System;
using System.Collections.Generic;
using WAI.Core;
using Xunit;

namespace WAI.Tests;

public class SearchTextTests
{
    [Fact]
    public void Clean_RemovesLoneSurrogates_WithoutException()
    {
        // Lone high surrogate and lone low surrogate
        string corrupted = "Valid text \uD800 and another \uDFFF piece of text";
        string cleaned = TextSanitize.Clean(corrupted);

        Assert.NotNull(cleaned);
        Assert.DoesNotContain("\uD800", cleaned);
        Assert.DoesNotContain("\uDFFF", cleaned);
        Assert.Contains("Valid text", cleaned);
    }

    [Fact]
    public void Terms_FiltersRussianStopwords_KeepsRussianKeywords()
    {
        string query = "найди мне важный отчет по бюджету за прошлый год";
        List<string> terms = Searcher.Terms(query);

        // Stopwords like "мне", "по", "за" should be removed
        Assert.DoesNotContain("мне", terms);
        Assert.DoesNotContain("по", terms);
        Assert.DoesNotContain("за", terms);

        // Keywords like "отчет", "бюджету" (or normalized forms) should remain
        Assert.Contains("важный", terms);
        Assert.Contains("отчет", terms);
        Assert.Contains("бюджету", terms);
        Assert.Contains("прошлый", terms);
        Assert.Contains("год", terms);
    }

    [Fact]
    public void Terms_MixedRussianAndEnglish_ExtractsBoth()
    {
        string query = "ошибка payment declined в чеке";
        List<string> terms = Searcher.Terms(query);

        Assert.Contains("ошибка", terms);
        Assert.Contains("payment", terms);
        Assert.Contains("declined", terms);
        Assert.Contains("чеке", terms);
        Assert.DoesNotContain("в", terms); // stopword
    }
}
