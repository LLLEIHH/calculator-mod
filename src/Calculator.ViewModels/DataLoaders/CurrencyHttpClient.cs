// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;

namespace CalculatorApp.ViewModel.DataLoaders
{
    /// <summary>
    /// 货币汇率数据源。
    ///
    /// 原实现使用欧洲央行（ECB）的 eurofxref-daily.xml 及其 aka.ms 转发地址，
    /// 在中国大陆网络环境下基本无法稳定访问；上游转发地址失效后，
    /// 本仓库退化为返回虚构货币（火星元、月球币…）的假数据。
    ///
    /// 现改为使用 currency-api（以 USD 为基准，jsDelivr CDN 分发），
    /// 该 CDN 在中国大陆可直连；若主地址失败则切换 Cloudflare Pages 镜像，
    /// 再失败则使用内置汇率快照，保证离线状态下货币转换依旧可用。
    /// </summary>
    public sealed class CurrencyHttpClient
    {
#if VIEWMODEL_FOR_UT
        public static bool ForceWebFailure = false;
#endif

        // Keep HttpClient field to ensure Windows.Web.Http assembly is referenced
        // (required for .NET Native AOT compilation stability).
        private static readonly Windows.Web.Http.HttpClient _sharedClient = new Windows.Web.Http.HttpClient();
        private string _responseLanguage;

        // 汇率接口：currency-api，{ date, usd: { cny: 7.08, eur: 0.92, ... } }
        // usd 下每个值表示 1 美元可兑换的该货币数量，正好对应 CURRENCY_CONVERTER_ALL_RATIOS_DATA 的 Rt 语义。
        private const string RatesUrlPrimary = "https://cdn.jsdelivr.net/npm/@fawazahmed0/currency-api@latest/v1/currencies/usd.min.json";
        private const string RatesUrlMirror = "https://latest.currency-api.pages.dev/v1/currencies/usd.min.json";

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

        // 支持的 36 种货币（与下方 Metadata 一一对应）
        private static readonly HashSet<string> SupportedCurrencyCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            "USD", "CNY", "EUR", "GBP", "JPY", "HKD", "MOP", "TWD", "KRW", "SGD",
            "AUD", "CAD", "CHF", "RUB", "INR", "BRL", "THB", "MYR", "IDR", "PHP",
            "VND", "NZD", "SEK", "NOK", "DKK", "PLN", "TRY", "ZAR", "MXN", "AED",
            "SAR", "ILS", "EGP", "PKR", "KZT", "NGN"
        };

        // 静态货币信息（国家/地区、货币代码、货币名称、符号）。
        // 原版由服务端下发，这里内置真实货币列表，离线也可用。
        private const string CurrencyStaticData = @"[
  { ""CountryCode"": ""USA"", ""CountryName"": ""美国"", ""CurrencyCode"": ""USD"", ""CurrencyName"": ""美元"", ""CurrencySymbol"": ""$"" },
  { ""CountryCode"": ""CHN"", ""CountryName"": ""中国"", ""CurrencyCode"": ""CNY"", ""CurrencyName"": ""人民币"", ""CurrencySymbol"": ""¥"" },
  { ""CountryCode"": ""EUR"", ""CountryName"": ""欧元区"", ""CurrencyCode"": ""EUR"", ""CurrencyName"": ""欧元"", ""CurrencySymbol"": ""€"" },
  { ""CountryCode"": ""GBR"", ""CountryName"": ""英国"", ""CurrencyCode"": ""GBP"", ""CurrencyName"": ""英镑"", ""CurrencySymbol"": ""£"" },
  { ""CountryCode"": ""JPN"", ""CountryName"": ""日本"", ""CurrencyCode"": ""JPY"", ""CurrencyName"": ""日元"", ""CurrencySymbol"": ""¥"" },
  { ""CountryCode"": ""HKG"", ""CountryName"": ""中国香港"", ""CurrencyCode"": ""HKD"", ""CurrencyName"": ""港元"", ""CurrencySymbol"": ""HK$"" },
  { ""CountryCode"": ""MAC"", ""CountryName"": ""中国澳门"", ""CurrencyCode"": ""MOP"", ""CurrencyName"": ""澳门元"", ""CurrencySymbol"": ""MOP$"" },
  { ""CountryCode"": ""TWN"", ""CountryName"": ""中国台湾"", ""CurrencyCode"": ""TWD"", ""CurrencyName"": ""新台币"", ""CurrencySymbol"": ""NT$"" },
  { ""CountryCode"": ""KOR"", ""CountryName"": ""韩国"", ""CurrencyCode"": ""KRW"", ""CurrencyName"": ""韩元"", ""CurrencySymbol"": ""₩"" },
  { ""CountryCode"": ""SGP"", ""CountryName"": ""新加坡"", ""CurrencyCode"": ""SGD"", ""CurrencyName"": ""新加坡元"", ""CurrencySymbol"": ""S$"" },
  { ""CountryCode"": ""AUS"", ""CountryName"": ""澳大利亚"", ""CurrencyCode"": ""AUD"", ""CurrencyName"": ""澳大利亚元"", ""CurrencySymbol"": ""A$"" },
  { ""CountryCode"": ""CAN"", ""CountryName"": ""加拿大"", ""CurrencyCode"": ""CAD"", ""CurrencyName"": ""加拿大元"", ""CurrencySymbol"": ""C$"" },
  { ""CountryCode"": ""CHE"", ""CountryName"": ""瑞士"", ""CurrencyCode"": ""CHF"", ""CurrencyName"": ""瑞士法郎"", ""CurrencySymbol"": ""CHF"" },
  { ""CountryCode"": ""RUS"", ""CountryName"": ""俄罗斯"", ""CurrencyCode"": ""RUB"", ""CurrencyName"": ""俄罗斯卢布"", ""CurrencySymbol"": ""₽"" },
  { ""CountryCode"": ""IND"", ""CountryName"": ""印度"", ""CurrencyCode"": ""INR"", ""CurrencyName"": ""印度卢比"", ""CurrencySymbol"": ""₹"" },
  { ""CountryCode"": ""BRA"", ""CountryName"": ""巴西"", ""CurrencyCode"": ""BRL"", ""CurrencyName"": ""巴西雷亚尔"", ""CurrencySymbol"": ""R$"" },
  { ""CountryCode"": ""THA"", ""CountryName"": ""泰国"", ""CurrencyCode"": ""THB"", ""CurrencyName"": ""泰铢"", ""CurrencySymbol"": ""฿"" },
  { ""CountryCode"": ""MYS"", ""CountryName"": ""马来西亚"", ""CurrencyCode"": ""MYR"", ""CurrencyName"": ""林吉特"", ""CurrencySymbol"": ""RM"" },
  { ""CountryCode"": ""IDN"", ""CountryName"": ""印度尼西亚"", ""CurrencyCode"": ""IDR"", ""CurrencyName"": ""印尼盾"", ""CurrencySymbol"": ""Rp"" },
  { ""CountryCode"": ""PHL"", ""CountryName"": ""菲律宾"", ""CurrencyCode"": ""PHP"", ""CurrencyName"": ""菲律宾比索"", ""CurrencySymbol"": ""₱"" },
  { ""CountryCode"": ""VNM"", ""CountryName"": ""越南"", ""CurrencyCode"": ""VND"", ""CurrencyName"": ""越南盾"", ""CurrencySymbol"": ""₫"" },
  { ""CountryCode"": ""NZL"", ""CountryName"": ""新西兰"", ""CurrencyCode"": ""NZD"", ""CurrencyName"": ""新西兰元"", ""CurrencySymbol"": ""NZ$"" },
  { ""CountryCode"": ""SWE"", ""CountryName"": ""瑞典"", ""CurrencyCode"": ""SEK"", ""CurrencyName"": ""瑞典克朗"", ""CurrencySymbol"": ""kr"" },
  { ""CountryCode"": ""NOR"", ""CountryName"": ""挪威"", ""CurrencyCode"": ""NOK"", ""CurrencyName"": ""挪威克朗"", ""CurrencySymbol"": ""kr"" },
  { ""CountryCode"": ""DNK"", ""CountryName"": ""丹麦"", ""CurrencyCode"": ""DKK"", ""CurrencyName"": ""丹麦克朗"", ""CurrencySymbol"": ""kr"" },
  { ""CountryCode"": ""POL"", ""CountryName"": ""波兰"", ""CurrencyCode"": ""PLN"", ""CurrencyName"": ""波兰兹罗提"", ""CurrencySymbol"": ""zł"" },
  { ""CountryCode"": ""TUR"", ""CountryName"": ""土耳其"", ""CurrencyCode"": ""TRY"", ""CurrencyName"": ""土耳其里拉"", ""CurrencySymbol"": ""₺"" },
  { ""CountryCode"": ""ZAF"", ""CountryName"": ""南非"", ""CurrencyCode"": ""ZAR"", ""CurrencyName"": ""南非兰特"", ""CurrencySymbol"": ""R"" },
  { ""CountryCode"": ""MEX"", ""CountryName"": ""墨西哥"", ""CurrencyCode"": ""MXN"", ""CurrencyName"": ""墨西哥比索"", ""CurrencySymbol"": ""MX$"" },
  { ""CountryCode"": ""ARE"", ""CountryName"": ""阿联酋"", ""CurrencyCode"": ""AED"", ""CurrencyName"": ""阿联酋迪拉姆"", ""CurrencySymbol"": ""AED"" },
  { ""CountryCode"": ""SAU"", ""CountryName"": ""沙特阿拉伯"", ""CurrencyCode"": ""SAR"", ""CurrencyName"": ""沙特里亚尔"", ""CurrencySymbol"": ""SAR"" },
  { ""CountryCode"": ""ISR"", ""CountryName"": ""以色列"", ""CurrencyCode"": ""ILS"", ""CurrencyName"": ""新谢克尔"", ""CurrencySymbol"": ""₪"" },
  { ""CountryCode"": ""EGY"", ""CountryName"": ""埃及"", ""CurrencyCode"": ""EGP"", ""CurrencyName"": ""埃及镑"", ""CurrencySymbol"": ""E£"" },
  { ""CountryCode"": ""PAK"", ""CountryName"": ""巴基斯坦"", ""CurrencyCode"": ""PKR"", ""CurrencyName"": ""巴基斯坦卢比"", ""CurrencySymbol"": ""₨"" },
  { ""CountryCode"": ""KAZ"", ""CountryName"": ""哈萨克斯坦"", ""CurrencyCode"": ""KZT"", ""CurrencyName"": ""坚戈"", ""CurrencySymbol"": ""₸"" },
  { ""CountryCode"": ""NGA"", ""CountryName"": ""尼日利亚"", ""CurrencyCode"": ""NGN"", ""CurrencyName"": ""奈拉"", ""CurrencySymbol"": ""₦"" }
]";

        // 内置汇率快照（1 美元可兑换数量），用于完全离线时的兜底。数据日期 2026-10-17。
        private const string FallbackRateDate = "2026-10-17";
        private static readonly string[] FallbackRates =
        {
            "USD=1", "CNY=6.687302", "EUR=0.889757", "GBP=0.750247", "JPY=158.0274",
            "HKD=7.848473", "MOP=8.083927", "TWD=32.478967", "KRW=1334.708648", "SGD=1.279567",
            "AUD=1.439519", "CAD=1.414157", "CHF=0.824531", "RUB=82.617657", "INR=94.971105",
            "BRL=5.063774", "THB=33.456965", "MYR=4.084136", "IDR=18052.451516", "PHP=62.993355",
            "VND=25827.670772", "NZD=1.769348", "SEK=9.91474", "NOK=9.428242", "DKK=6.650994",
            "PLN=3.887552", "TRY=49.578559", "ZAR=16.479066", "MXN=18.491397", "AED=3.6725",
            "SAR=3.75", "ILS=3.094537", "EGP=53.160621", "PKR=276.881129", "KZT=452.340988",
            "NGN=1331.401918"
        };

        /* 单元测试使用固定数据，保证结果可复现 */
        private const string MockCurrencyStaticData = @"[
  { ""CountryCode"": ""MAR"", ""CountryName"": ""Mars"", ""CurrencyCode"": ""MAR"", ""CurrencyName"": ""The Martian Dollar"", ""CurrencySymbol"": ""¤"" },
  { ""CountryCode"": ""MON"", ""CountryName"": ""Moon"", ""CurrencyCode"": ""MON"", ""CurrencyName"": ""Moon Bucks"", ""CurrencySymbol"": ""¤"" },
  { ""CountryCode"": ""NEP"", ""CountryName"": ""Neptune"", ""CurrencyCode"": ""NEP"", ""CurrencyName"": ""Space Coins"", ""CurrencySymbol"": ""¤"" },
  { ""CountryCode"": ""SAT"", ""CountryName"": ""Saturn"", ""CurrencyCode"": ""SAT"", ""CurrencyName"": ""Rings"", ""CurrencySymbol"": ""¤"" },
  { ""CountryCode"": ""TEST1"", ""CountryName"": ""Test No Fractional Digits"", ""CurrencyCode"": ""JPY"", ""CurrencyName"": ""Test No Fractional Digits"", ""CurrencySymbol"": ""¤"" },
  { ""CountryCode"": ""TEST2"", ""CountryName"": ""Test Fractional Digits"", ""CurrencyCode"": ""JOD"", ""CurrencyName"": ""Test Fractional Digits"", ""CurrencySymbol"": ""¤"" }
]";

        private const string MockCurrencyConverterData = @"[
  { ""An"": ""MAR"", ""Rt"": 1.00 },
  { ""An"": ""MON"", ""Rt"": 0.50 },
  { ""An"": ""NEP"", ""Rt"": 0.00125 },
  { ""An"": ""SAT"", ""Rt"": 0.25 },
  { ""An"": ""JPY"", ""Rt"": 0.00125 },
  { ""An"": ""JOD"", ""Rt"": 0.25 }
]";

        public void Initialize(string sourceCurrencyCode, string responseLanguage)
        {
            _responseLanguage = responseLanguage;
        }

        public Task<string> GetCurrencyMetadataAsync()
        {
#if VIEWMODEL_FOR_UT
            if (ForceWebFailure)
            {
                return Task.FromException<string>(new Exception("Forced web failure for unit testing"));
            }

            return Task.FromResult(MockCurrencyStaticData);
#else
            return Task.FromResult(CurrencyStaticData);
#endif
        }

        public async Task<string> GetCurrencyRatiosAsync()
        {
#if VIEWMODEL_FOR_UT
            if (ForceWebFailure)
            {
                throw new Exception("Forced web failure for unit testing");
            }

            await Task.CompletedTask;
            return MockCurrencyConverterData;
#else
            Dictionary<string, double> rates = await TryFetchRatesAsync(RatesUrlPrimary);
            if (rates == null)
            {
                rates = await TryFetchRatesAsync(RatesUrlMirror);
            }

            double cnyRate;
            if (rates == null)
            {
                rates = LoadSnapshotRates();
                Debug.WriteLine("CurrencyHttpClient: 网络不可用，已回退到内置汇率快照（" + FallbackRateDate + "）。");
            }
            else if (rates.TryGetValue("CNY", out cnyRate))
            {
                Debug.WriteLine("CurrencyHttpClient: 已获取在线汇率，1 USD = " + cnyRate.ToString("F4", CultureInfo.InvariantCulture) + " CNY。");
            }

            return BuildRatiosJson(rates);
#endif
        }

        private static async Task<Dictionary<string, double>> TryFetchRatesAsync(string url)
        {
            try
            {
                using (var cts = new CancellationTokenSource(RequestTimeout))
                {
                    string json = await _sharedClient.GetStringAsync(new Uri(url)).AsTask(cts.Token);
                    Dictionary<string, double> parsed = TryParseRates(json);
                    if (parsed != null && parsed.Count > 1)
                    {
                        return parsed;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("CurrencyHttpClient: 请求 " + url + " 失败 - " + ex.Message);
            }

            return null;
        }

        private static Dictionary<string, double> TryParseRates(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            JsonObject root;
            if (!JsonObject.TryParse(json, out root))
            {
                return null;
            }

            IJsonValue usdValue;
            if (!root.TryGetValue("usd", out usdValue) || usdValue.ValueType != JsonValueType.Object)
            {
                return null;
            }

            var result = new Dictionary<string, double>(StringComparer.Ordinal) { { "USD", 1.0 } };
            foreach (KeyValuePair<string, IJsonValue> pair in usdValue.GetObject())
            {
                string code = pair.Key.ToUpperInvariant();
                if (!SupportedCurrencyCodes.Contains(code) || pair.Value.ValueType != JsonValueType.Number)
                {
                    continue;
                }

                double rate = pair.Value.GetNumber();
                if (rate > 0 && !double.IsNaN(rate) && !double.IsInfinity(rate))
                {
                    result[code] = rate;
                }
            }

            return result;
        }

        private static Dictionary<string, double> LoadSnapshotRates()
        {
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (string entry in FallbackRates)
            {
                int split = entry.IndexOf('=');
                if (split <= 0)
                {
                    continue;
                }

                double value;
                if (double.TryParse(entry.Substring(split + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    result[entry.Substring(0, split)] = value;
                }
            }

            return result;
        }

        private static string BuildRatiosJson(Dictionary<string, double> rates)
        {
            var builder = new StringBuilder(rates.Count * 32);
            builder.Append('[');

            bool first = true;
            foreach (KeyValuePair<string, double> rate in rates)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                builder.Append("{\"An\":\"").Append(rate.Key)
                       .Append("\",\"Rt\":")
                       .Append(rate.Value.ToString("R", CultureInfo.InvariantCulture))
                       .Append('}');
            }

            builder.Append(']');
            return builder.ToString();
        }
    }
}
