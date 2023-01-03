﻿// Copyright (c) SimpleIdServer. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimpleIdServer.Domains;
using SimpleIdServer.OAuth.Options;
using SimpleIdServer.Store;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleIdServer.OpenID.Metadata
{
    public interface IMetadataResultBuilder
    {
        IMetadataResultBuilder AddTranslatedEnum<T>(string name) where T : struct;
        Task<MetadataResult> Build(string language, CancellationToken cancellationToken);
    }

    public class MetadataResultBuilder : IMetadataResultBuilder
    {
        private readonly ITranslationRepository _translationRepository;
        private readonly OAuthHostOptions _options;
        private Dictionary<string, Type> _dic;

        public MetadataResultBuilder(
            IOptions<OAuthHostOptions> options, 
            ITranslationRepository translationRepository)
        {
            _options = options.Value;
            _translationRepository = translationRepository;
            _dic = new Dictionary<string, Type>();
        }

        public IMetadataResultBuilder AddTranslatedEnum<T>(string name) where T : struct
        {
            _dic.Add(name, typeof(T));
            return this;
        }

        public async Task<MetadataResult> Build(string language, CancellationToken cancellationToken)
        {
            var languages = _options.SupportedUICultures.Select(l => l.Name);
            var languageExists = !string.IsNullOrWhiteSpace(language) && languages.Contains(language);
            var translationCodes = new List<string>();
            var result = new MetadataResult();
            if (_dic.Any())
            {
                foreach(var kvp in _dic) 
                    translationCodes.AddRange(GetTranslationCodes(kvp.Value));

                IEnumerable<Translation> translations;
                if (languageExists)
                {
                    translations = await _translationRepository.Query().AsNoTracking().Where(t => translationCodes.Contains(t.Key) && t.Language == language).ToListAsync(cancellationToken);
                    languages = new[] { language };
                }
                else
                    translations = await _translationRepository.Query().AsNoTracking().Where(t => translationCodes.Contains(t.Key)).ToListAsync(cancellationToken);

                translations = SetDefaultValues(translationCodes, languages, translations);
                foreach (var kvp in _dic)
                    result.Content.Add(kvp.Key, BuildMetadataRecord(kvp.Value, language, translations));
            }

            return result;
        }

        private IEnumerable<Translation> SetDefaultValues(List<string> translationCodes, IEnumerable<string> languages, IEnumerable<Translation> existingTranslations)
        {
            var result = new List<Translation>();
            result.AddRange(existingTranslations);
            foreach(var lng in languages)
            {
                foreach(var translationCode in translationCodes)
                {
                    if (!result.Any(t => t.Key == translationCode && t.Language == lng))
                    {
                        result.Add(new Translation
                        {
                            Key = translationCode,
                            Language = lng,
                            Value = $"[{translationCode}]"
                        });
                    }
                }
            }
            return result;
        }

        private MetadataRecord BuildMetadataRecord(Type type, string defaultLanguage, IEnumerable<Translation> translations)
        {
            var names = Enum.GetNames(type);
            var result = new MetadataRecord();
            foreach(var name in names)
            {
                var child = new MetadataRecord();
                var value = GetValue(type, name);
                var trs = translations.Where(t => t.Key == GetTranslationCode(type, name));
                foreach (var tr in trs)
                {
                    child.Translations.Add(new TranslationResult
                    {
                        LanguageCode = tr.Language,
                        Value = tr.Value
                    });
                }

                result.Children.Add(value, child);
            }

            return result;
        }

        private static ICollection<string> GetTranslationCodes(Type type)
        {
            var names = Enum.GetNames(type);
            return names.Select(n => GetTranslationCode(type, n)).ToList();
        }

        private static string GetTranslationCode(Type type, string name) => $"{type.Name}_{name}";

        private static string GetValue(Type type, string name) => ((int)Enum.Parse(type, name)).ToString();
    }
}
