﻿// Copyright 2005-2015 Giacomo Stelluti Scala & Contributors. All rights reserved. See License.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using CommandLine.Infrastructure;
using CSharpx;
using RailwaySharp.ErrorHandling;
using System.Text.RegularExpressions;

namespace CommandLine.Core
{
    static class GetoptTokenizer
    {
        public static Result<IEnumerable<Token>, Error> Tokenize(
            IEnumerable<string> arguments,
            Func<string, NameLookupResult> nameLookup)
        {
            return GetoptTokenizer.Tokenize(arguments, nameLookup, ignoreUnknownArguments:false, allowDashDash:true, posixlyCorrect:false);
        }

        public static Result<IEnumerable<Token>, Error> Tokenize(
            IEnumerable<string> arguments,
            Func<string, NameLookupResult> nameLookup,
            bool ignoreUnknownArguments,
            bool allowDashDash,
            bool posixlyCorrect)
        {
            var errors = new List<Error>();
            Action<string> onBadFormatToken = arg => errors.Add(new BadFormatTokenError(arg));
            Action<string> unknownOptionError = name => errors.Add(new UnknownOptionError(name));
            Action<string> doNothing = name => {};
            Action<string> onUnknownOption = ignoreUnknownArguments ? doNothing : unknownOptionError;

            int consumeNext = 0;
            Action<int> onConsumeNext = (n => consumeNext = consumeNext + n);
            bool forceValues = false;

            var tokens = new List<Token>();

            var enumerator = arguments.GetEnumerator();
            while (enumerator.MoveNext())
            {
                switch (enumerator.Current) {
                    case null:
                        break;

                    case string arg when forceValues:
                        tokens.Add(Token.ValueForced(arg));
                        break;

                    case string arg when consumeNext > 0:
                        tokens.Add(Token.Value(arg));
                        consumeNext = consumeNext - 1;
                        break;

                    case "--" when allowDashDash:
                        forceValues = true;
                        break;

                    case "--":
                        tokens.Add(Token.Value("--"));
                        if (posixlyCorrect) forceValues = true;
                        break;

                    case "-":
                        // A single hyphen is always a value (it usually means "read from stdin" or "write to stdout")
                        tokens.Add(Token.Value("-"));
                        if (posixlyCorrect) forceValues = true;
                        break;

                    case string arg when arg.StartsWith("--"):
                        tokens.AddRange(TokenizeLongName(arg, nameLookup, onBadFormatToken, onUnknownOption, onConsumeNext));
                        break;

                    case string arg when arg.StartsWith("-"):
                        tokens.AddRange(TokenizeLongName(arg, nameLookup, onBadFormatToken, onUnknownOption, onConsumeNext, 1));
                        break;

                    case string arg:
                        // If we get this far, it's a plain value
                        tokens.Add(Token.Value(arg));
                        if (posixlyCorrect) forceValues = true;
                        break;
                }
            }

            return Result.Succeed<IEnumerable<Token>, Error>(tokens.AsEnumerable(), errors.AsEnumerable());
        }

        public static Result<IEnumerable<Token>, Error> ExplodeOptionList(
            Result<IEnumerable<Token>, Error> tokenizerResult,
            Func<string, Maybe<char>> optionSequenceWithSeparatorLookup)
        {
            var tokens = tokenizerResult.SucceededWith().Memoize();

            var exploded = new List<Token>(tokens is ICollection<Token> coll ? coll.Count : tokens.Count());
            var nothing = Maybe.Nothing<char>();  // Re-use same Nothing instance for efficiency
            var separator = nothing;
            foreach (var token in tokens) {
                if (token.IsName()) {
                    separator = optionSequenceWithSeparatorLookup(token.Text);
                    exploded.Add(token);
                } else {
                    // Forced values are never considered option values, so they should not be split
                    if (separator.MatchJust(out char sep) && sep != '\0' && !token.IsValueForced()) {
                        if (token.Text.Contains(sep)) {
                            exploded.AddRange(token.Text.Split(sep).Select(Token.ValueFromSeparator));
                        } else {
                            exploded.Add(token);
                        }
                    } else {
                        exploded.Add(token);
                    }
                    separator = nothing;  // Only first value after a separator can possibly be split
                }
            }
            return Result.Succeed(exploded as IEnumerable<Token>, tokenizerResult.SuccessMessages());
        }

        public static Func<
                    IEnumerable<string>,
                    IEnumerable<OptionSpecification>,
                    Result<IEnumerable<Token>, Error>>
            ConfigureTokenizer(
                    StringComparer nameComparer,
                    bool ignoreUnknownArguments,
                    bool enableDashDash,
                    bool posixlyCorrect)
        {
            return (arguments, optionSpecs) =>
                {
                    var tokens = GetoptTokenizer.Tokenize(arguments, name => NameLookup.Contains(name, optionSpecs, nameComparer), ignoreUnknownArguments, enableDashDash, posixlyCorrect);
                    var explodedTokens = GetoptTokenizer.ExplodeOptionList(tokens, name => NameLookup.HavingSeparator(name, optionSpecs, nameComparer));
                    return explodedTokens;
                };
        }
        private static IEnumerable<Token> TokenizeLongName(
            string arg,
            Func<string, NameLookupResult> nameLookup,
            Action<string> onBadFormatToken,
            Action<string> onUnknownOption,
            Action<int> onConsumeNext,
            int hyphens = 2)
        {
            string[] parts = arg.Substring(hyphens).Split(new char[] { '=' }, 2);
            string name = parts[0];
            string value = (parts.Length > 1) ? parts[1] : null;
            // A parameter like "--stringvalue=" is acceptable, and makes stringvalue be the empty string
            if (String.IsNullOrWhiteSpace(name) || name.Contains(" "))
            {
                onBadFormatToken(arg);
                yield break;
            }
            switch(nameLookup(name))
            {
                case NameLookupResult.NoOptionFound:
                    onUnknownOption(name);
                    yield break;

                case NameLookupResult.OtherOptionFound:
                    yield return Token.Name(name);
                    if (value == null) // NOT String.IsNullOrEmpty
                    {
                        onConsumeNext(1);
                    }
                    else
                    {
                        yield return Token.Value(value);
                    }
                    break;

                default:
                    yield return Token.Name(name);
                    break;
            }
        }
    }
}
