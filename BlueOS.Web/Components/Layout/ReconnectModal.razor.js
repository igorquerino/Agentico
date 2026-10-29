// Set up event handlers
const reconnectModal = document.getElementById("components-reconnect-modal");
reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);

const retryButton = document.getElementById("components-reconnect-button");
retryButton.addEventListener("click", retry);

const resumeButton = document.getElementById("components-resume-button");
resumeButton.addEventListener("click", resume);

function handleReconnectStateChanged(event) {
    if (event.detail.state === "show") {
        reconnectModal.showModal();
    } else if (event.detail.state === "hide") {
        reconnectModal.close();
    } else if (event.detail.state === "failed") {
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    } else if (event.detail.state === "rejected") {
        location.reload();
    }
}

async function retry() {
    document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);

    try {
        // Reconnect will asynchronously return:
        // - true to mean success
        // - false to mean we reached the server, but it rejected the connection (e.g., unknown circuit ID)
        // - exception to mean we didn't reach the server (this can be sync or async)
        const successful = await Blazor.reconnect();
        if (!successful) {
            // We have been able to reach the server, but the circuit is no longer available.
            // We'll reload the page so the user can continue using the app as quickly as possible.
            const resumeSuccessful = await Blazor.resumeCircuit();
            if (!resumeSuccessful) {
                location.reload();
            } else {
                reconnectModal.close();
            }
        }
    } catch (err) {
        // We got an exception, server is currently unavailable
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    }
}

async function resume() {
    try {
        const successful = await Blazor.resumeCircuit();
        if (!successful) {
            location.reload();
        }
    } catch {
        location.reload();
    }
}

async function retryWhenDocumentBecomesVisible() {
    if (document.visibilityState === "visible") {
        await retry();
    }
}

// SIG // Begin signature block
// SIG // MIInMwYJKoZIhvcNAQcCoIInJDCCJyACAQExDzANBglg
// SIG // hkgBZQMEAgEFADB3BgorBgEEAYI3AgEEoGkwZzAyBgor
// SIG // BgEEAYI3AgEeMCQCAQEEEBDgyQbOONQRoqMAEEvTUJAC
// SIG // AQACAQACAQACAQACAQAwMTANBglghkgBZQMEAgEFAAQg
// SIG // UTXnMp4RSOnE0gLT8zOiVFbdxnUM+Wvr+fzFn1wXFtCg
// SIG // ggypMIIF5DCCA8ygAwIBAgITMwAAAcgkOsgeTZOIuwAA
// SIG // AAAByDANBgkqhkiG9w0BAQsFADBXMQswCQYDVQQGEwJV
// SIG // UzEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9u
// SIG // MSgwJgYDVQQDEx9NaWNyb3NvZnQgQ29kZSBTaWduaW5n
// SIG // IFBDQSAyMDI0MB4XDTI2MDQxNjE4NTc0MVoXDTI3MDQx
// SIG // NTE4NTc0MVowYzELMAkGA1UEBhMCVVMxEzARBgNVBAgT
// SIG // Cldhc2hpbmd0b24xEDAOBgNVBAcTB1JlZG1vbmQxHjAc
// SIG // BgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjENMAsG
// SIG // A1UEAxMELk5FVDCCASIwDQYJKoZIhvcNAQEBBQADggEP
// SIG // ADCCAQoCggEBAMJfsAP8OxCUF6ivbFnD75BCsACvUPfi
// SIG // Af/LQ2AsqaCvJWtki//fPx4BhRWbvVuA5Tn1RvESIwAA
// SIG // oBJic0fJfjHYrZeQ4Ga7AxdGESwJMP5LZYfbVYKef3k/
// SIG // slhO8ygk5JNvxgos4lfXPCoBDFNm9a0fHfrRjgEVOmLb
// SIG // andQPFM0MSKk+kiJtSIpB3dIWQhaMQ3t0XwVHo3XEDYo
// SIG // SY3qU/npr6shcxBFgdZcTf0AQsITzCHPFDUS+iPqdmLh
// SIG // /PIokqOhmxIgd5Q69vqYjASwq1sg9peqfsJKVY4C0idy
// SIG // /Uf4AtWU6qmfxbQGrxphgBM4YU21HyQ1CgtEscHdSEKk
// SIG // TrcCAwEAAaOCAZswggGXMA4GA1UdDwEB/wQEAwIHgDAf
// SIG // BgNVHSUEGDAWBgorBgEEAYI3TAgBBggrBgEFBQcDAzAd
// SIG // BgNVHQ4EFgQUwduFP95X0NdybDKqpv9jV3T9cfIwRQYD
// SIG // VR0RBD4wPKQ6MDgxHjAcBgNVBAsTFU1pY3Jvc29mdCBD
// SIG // b3Jwb3JhdGlvbjEWMBQGA1UEBRMNNDY0MjIzKzUwNzU5
// SIG // NjAfBgNVHSMEGDAWgBR/WT9UIdqtT+8F5eaj1y0GlBII
// SIG // MTBgBgNVHR8EWTBXMFWgU6BRhk9odHRwOi8vd3d3Lm1p
// SIG // Y3Jvc29mdC5jb20vcGtpb3BzL2NybC9NaWNyb3NvZnQl
// SIG // MjBDb2RlJTIwU2lnbmluZyUyMFBDQSUyMDIwMjQuY3Js
// SIG // MG0GCCsGAQUFBwEBBGEwXzBdBggrBgEFBQcwAoZRaHR0
// SIG // cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9wcy9jZXJ0
// SIG // cy9NaWNyb3NvZnQlMjBDb2RlJTIwU2lnbmluZyUyMFBD
// SIG // QSUyMDIwMjQuY3J0MAwGA1UdEwEB/wQCMAAwDQYJKoZI
// SIG // hvcNAQELBQADggIBAKNTJ0R6r3T/SAhdOUibRuJvosAa
// SIG // IssCd+bb8aUe/hDVbBovDkE3EtgWAm5CPeFOeYiqmpCt
// SIG // 3aJntyMvA/qtLuWeJymwvCd4E/qo2If/trHxzZ12yGjo
// SIG // xp822b46DKIFFbg0mvZ74f0WVRu+Ftae93BT4Hid2x0h
// SIG // MYQWtIgTjoGH0FJCETvfTBBgegscj1AY+zghBpgeK2Df
// SIG // 5+zZdCGr6s/s6/xecllnuoJIdD35AW4G/5yqjU7oztCZ
// SIG // Wzr21L1Dlsq6RYuFg/2yym7hd9V+Au6XojrKbpR9Vi5v
// SIG // HqqWXjmE6QF/BXRSXzNHmqZhtCjKynbcMHNeQkqy+5/3
// SIG // iPSvAs8Fd6oVHRJxc+CFCWVirnpm9tQHNvfyvEsjTxjm
// SIG // e5pyWvnz6bGQM8bm30Zto/6F1zviqMTCJ2+OORn3wFZv
// SIG // F+63NT4UB9FH7SxjdgFZoprwvQxnzvCujaOfosARxt/7
// SIG // DrOF26bvdUNRsJQSjhdzGVZp5nLCT6aYONqVI75oljrJ
// SIG // kBNTJ2nmNYeUxUKV2iyntZqIUIqHd+SHDAn+GcoI+Jo1
// SIG // 1J3pOuCTl6OygDdPJZW5UfcglKCfdoHSzFAEH/dvpT+U
// SIG // 8sA3xIseAAsH3nUjOwadeMLu1jkRC40u7CcVfqaDuUO9
// SIG // 58az54AQfjwWRT3JxzVAJT1v0BX6gkkyemgCcawdMIIG
// SIG // vTCCBKWgAwIBAgITMwAAADk7tjcZvwYdZwAAAAAAOTAN
// SIG // BgkqhkiG9w0BAQwFADCBiDELMAkGA1UEBhMCVVMxEzAR
// SIG // BgNVBAgTCldhc2hpbmd0b24xEDAOBgNVBAcTB1JlZG1v
// SIG // bmQxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlv
// SIG // bjEyMDAGA1UEAxMpTWljcm9zb2Z0IFJvb3QgQ2VydGlm
// SIG // aWNhdGUgQXV0aG9yaXR5IDIwMTEwHhcNMjQwODA4MjA1
// SIG // NDE4WhcNMzYwMzIyMjIxMzA0WjBXMQswCQYDVQQGEwJV
// SIG // UzEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9u
// SIG // MSgwJgYDVQQDEx9NaWNyb3NvZnQgQ29kZSBTaWduaW5n
// SIG // IFBDQSAyMDI0MIICIjANBgkqhkiG9w0BAQEFAAOCAg8A
// SIG // MIICCgKCAgEA2AGcHuM4x6qVFgc1rbrm/ghj18fxMqF6
// SIG // Yd88g17WCktpXd2GDfbhbAfTotwdumewG6QIM2K2vKjR
// SIG // 21L8Rboj/IQv9stJjyEqlt9C5a6wE+v2prNhwPEJb+qe
// SIG // NvkmwjWzxs06DdtUBO9BUvi1x/TdpPJyzPxB4J0zSX/I
// SIG // dE5sn1CprMzWvpU8Q4nssv64QRvvfDpAK6Gzz1rW6/XN
// SIG // 6s5gyeyd5FHAJunJbXUhyCBTRxEoMOrWmNUnMhsgXr6i
// SIG // JddtF46yB4jzO7UXJB8rR9WRrJrxKZUdD+05/beZnhb2
// SIG // TRGLbZBb2ndSBILk5QOS0rHFwKYYvq1ct84ZJYcghXhi
// SIG // tMlNPo823LlESiMcm5kcCuQX1WcdMRFahOMDN8jeQ/7l
// SIG // vhqVR2GABnu2067VtWdd8dqo9iXas+zbSOLTcs5VayH+
// SIG // tp2ATXt6zmEv63qVXR5UetWGyyxE5Ym7PYxcwK3uLDuU
// SIG // U8b0tcoaOyaiefaCi0Z0ci4SDkmckwlWaLF3ktGWSaBh
// SIG // FkrOHFILDKgYirQ+FoDtj5U9y3mkIeSKNwggObSeWQr7
// SIG // QrJ6miVyoabRP8ZhBEyEcmUY46ZVCinfrBRVRiSVTL76
// SIG // 8NZ4SASjizuHE3qYht/YxIhD+Ih8xmAnELr2i6QxRcKs
// SIG // 4LdKQT/EiSCl+XbYwzWK2Rnfmc1eQyiVTWUCAwEAAaOC
// SIG // AU4wggFKMA4GA1UdDwEB/wQEAwIBhjAQBgkrBgEEAYI3
// SIG // FQEEAwIBADAdBgNVHQ4EFgQUf1k/VCHarU/vBeXmo9ct
// SIG // BpQSCDEwGQYJKwYBBAGCNxQCBAweCgBTAHUAYgBDAEEw
// SIG // DwYDVR0TAQH/BAUwAwEB/zAfBgNVHSMEGDAWgBRyLToC
// SIG // MZBDuRQFTuHqp8cx0SOJNDBaBgNVHR8EUzBRME+gTaBL
// SIG // hklodHRwOi8vY3JsLm1pY3Jvc29mdC5jb20vcGtpL2Ny
// SIG // bC9wcm9kdWN0cy9NaWNSb29DZXJBdXQyMDExXzIwMTFf
// SIG // MDNfMjIuY3JsMF4GCCsGAQUFBwEBBFIwUDBOBggrBgEF
// SIG // BQcwAoZCaHR0cDovL3d3dy5taWNyb3NvZnQuY29tL3Br
// SIG // aS9jZXJ0cy9NaWNSb29DZXJBdXQyMDExXzIwMTFfMDNf
// SIG // MjIuY3J0MA0GCSqGSIb3DQEBDAUAA4ICAQAUlB84KE/u
// SIG // iefp8sgwqtKU3VZgrAMWAB13KY5Q7cWszx3sH9b+JDoP
// SIG // FewOfsPlbjAzBh4vKy1wSp+SPPg1RFGBrPIy7nJHNCHg
// SIG // uqMDi1K1NwmHWikTGjuefk+48Fidu7T5MdK5UdN7RVNM
// SIG // 9WGKXL+mIWsOjdrFD0/gL46XnJ637aBN96QgJLnFL5xh
// SIG // 9Ii+CfQmSxUFUxhUjlAW7+qGcuGwQURTMbx++/SGOCQ7
// SIG // 6WSlX23LoaQ3i92d3vJrpDppH3LfhqIzWqbFrEGLo5Sf
// SIG // I2Xp+S66f92JMWdgMtOmk6Sv+aDlZJ8KINUw0LG2PjA8
// SIG // oLk6YebUNAi38w2iRtsfdQawU/VBvOwuhy5KosK8fT0i
// SIG // jd8M9OaxxH1jvkbipftFNfwB0E+jQjo4SiN/f3O4Vm3S
// SIG // o4ebrlhZATr1xkza54TUwHTl002Acr2BMTvMq8r9+DwH
// SIG // aqNbzwxP9YXlXm69ka2pr0VIvZFrMCsD6sM+5/okZjPg
// SIG // emAxkcHhLqzNZIpgG/RWKwLN/GB5T52q5db1t3Rq5iU4
// SIG // HnwM9w5gp1zdJ73iD7EvilwSFsHngk6ACTBhO7/10t4f
// SIG // akOp4lkAAFUNZFAJpd87kuDIrAoIthemKCtlgKNRIFyv
// SIG // 5V7w8VYyFVNCXS/irwn8BSZA3lbifXTVxjYvgDsZNAbW
// SIG // HfYccC99ARJY/TGCGeIwghneAgEBMG4wVzELMAkGA1UE
// SIG // BhMCVVMxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3Jh
// SIG // dGlvbjEoMCYGA1UEAxMfTWljcm9zb2Z0IENvZGUgU2ln
// SIG // bmluZyBQQ0EgMjAyNAITMwAAAcgkOsgeTZOIuwAAAAAB
// SIG // yDANBglghkgBZQMEAgEFAKCBrjAZBgkqhkiG9w0BCQMx
// SIG // DAYKKwYBBAGCNwIBBDAcBgorBgEEAYI3AgELMQ4wDAYK
// SIG // KwYBBAGCNwIBFTAvBgkqhkiG9w0BCQQxIgQg2FjisxuK
// SIG // gRXJx2SFwsHqZStw/lDWOj8AwrsPw6cfVXwwQgYKKwYB
// SIG // BAGCNwIBDDE0MDKgFIASAE0AaQBjAHIAbwBzAG8AZgB0
// SIG // oRqAGGh0dHA6Ly93d3cubWljcm9zb2Z0LmNvbTANBgkq
// SIG // hkiG9w0BAQEFAASCAQBEdeRsjczqz4TPoiJSbb06+0W3
// SIG // 4d1ayJms3eA6U63xeaMGTbCqwKGJ8tahx8nhqeOnWYem
// SIG // 4mTcmwbBCH5g0wwsjQyO5WPjcrPlWeM49QuKrUUWsgfN
// SIG // zcGGnbB8ngAeImuD97v19JXEBKfjmDcYASeGGxMsYcd6
// SIG // Wcb2+H7lZLDZ27xR2CXC5VdnNMs/KcPMn0YmlnzUYzib
// SIG // SLlOdNs4CN6lKU1YExu+US4t6jtApXLV8cv+TooqhLPz
// SIG // 1YfXlOOLcAXU9LDrpPpHonq3KQY62YOXPv07XkPCFctj
// SIG // qlFza0zyU6NE7FeDRSjaiRz1c9599YQQOn04fqrhW+Tn
// SIG // Slli2E0uoYIXlDCCF5AGCisGAQQBgjcDAwExgheAMIIX
// SIG // fAYJKoZIhvcNAQcCoIIXbTCCF2kCAQMxDzANBglghkgB
// SIG // ZQMEAgEFADCCAVIGCyqGSIb3DQEJEAEEoIIBQQSCAT0w
// SIG // ggE5AgEBBgorBgEEAYRZCgMBMDEwDQYJYIZIAWUDBAIB
// SIG // BQAEIJx63ogv4Kf/0BGOXGRnNQTJzWxxQzXZ8+uEDoR7
// SIG // bUo+AgZqqivOXEwYEzIwMjYwOTIxMTcxODI5Ljc1M1ow
// SIG // BIACAfSggdGkgc4wgcsxCzAJBgNVBAYTAlVTMRMwEQYD
// SIG // VQQIEwpXYXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRtb25k
// SIG // MR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24x
// SIG // JTAjBgNVBAsTHE1pY3Jvc29mdCBBbWVyaWNhIE9wZXJh
// SIG // dGlvbnMxJzAlBgNVBAsTHm5TaGllbGQgVFNTIEVTTjo4
// SIG // OTAwLTA1RTAtRDk0NzElMCMGA1UEAxMcTWljcm9zb2Z0
// SIG // IFRpbWUtU3RhbXAgU2VydmljZaCCEeowggcgMIIFCKAD
// SIG // AgECAhMzAAACIkHS9qr/yLX/AAEAAAIiMA0GCSqGSIb3
// SIG // DQEBCwUAMHwxCzAJBgNVBAYTAlVTMRMwEQYDVQQIEwpX
// SIG // YXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRtb25kMR4wHAYD
// SIG // VQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xJjAkBgNV
// SIG // BAMTHU1pY3Jvc29mdCBUaW1lLVN0YW1wIFBDQSAyMDEw
// SIG // MB4XDTI2MDIxOTE5Mzk1NloXDTI3MDUxNzE5Mzk1Nlow
// SIG // gcsxCzAJBgNVBAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5n
// SIG // dG9uMRAwDgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVN
// SIG // aWNyb3NvZnQgQ29ycG9yYXRpb24xJTAjBgNVBAsTHE1p
// SIG // Y3Jvc29mdCBBbWVyaWNhIE9wZXJhdGlvbnMxJzAlBgNV
// SIG // BAsTHm5TaGllbGQgVFNTIEVTTjo4OTAwLTA1RTAtRDk0
// SIG // NzElMCMGA1UEAxMcTWljcm9zb2Z0IFRpbWUtU3RhbXAg
// SIG // U2VydmljZTCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCC
// SIG // AgoCggIBALW54om6Qi5SwAAmj8BjkNlGoftuGC/sJYY2
// SIG // UR1tEaghOt0Tpayfns1o27UFN2MFsVy/tF+LG17TH4dG
// SIG // 9dKqwP5Z5Jf/r/L3ATQzP7FE9MYhjbQrtpANrrw7LNXJ
// SIG // R5QLKnJkL+Bb/fK079k6dT0fauLvuQk/wAGurLLVTFf8
// SIG // 6x4SC8eyPzKKRZPQBG2uNZtcwcXNI6jmFBx9SYxcqpZb
// SIG // Pr43T5TKeEbLWf52hbhZmCkfxjlbuGlKiRaPUz8u7jCL
// SIG // ejoPP29Va6RyBQUaMsCXhhmk6FqHse6IL9qVciYxB/wL
// SIG // cDyr/r/WEWh4hkHhQaTLDEH85JM5Kwvr7f2kOrMzsKA6
// SIG // l/hXv32Q33jIz25ckjlP9KIDkx0hkiERbT5uHzlGoOHl
// SIG // hbf+hq/nhE/HDk4+UfrhBXoomSXQUgSUxWgs2jxRZFBw
// SIG // wPXv3HtYBKMLouxo1nvIrSpwRIiwvXCJCZ19AHFyqsUK
// SIG // khB+eZAWQ6n0jJdRarNry2anPwTppeD1vV6IBPc9VOCs
// SIG // 6U+L+FhkJ8/Ff/qMa3I+PLUKLA6YlqaiGZJT/8I4B6d9
// SIG // FPYbYcxFSkJfXOz4CYOZ1AzVdFpvhhIAssCUPMYKyAjv
// SIG // uee4mOhcCWIma/s1+u9YBwDkqoJQ5ZDqRI+3mvbwx8pd
// SIG // YkmlJe0V5L8yQPMnL+IlFXIdwXL8H4y3AgMBAAGjggFJ
// SIG // MIIBRTAdBgNVHQ4EFgQUWQfAagMnllsQSK7wqy2K6ypq
// SIG // jNAwHwYDVR0jBBgwFoAUn6cVXQBeYl2D9OXSZacbUzUZ
// SIG // 6XIwXwYDVR0fBFgwVjBUoFKgUIZOaHR0cDovL3d3dy5t
// SIG // aWNyb3NvZnQuY29tL3BraW9wcy9jcmwvTWljcm9zb2Z0
// SIG // JTIwVGltZS1TdGFtcCUyMFBDQSUyMDIwMTAoMSkuY3Js
// SIG // MGwGCCsGAQUFBwEBBGAwXjBcBggrBgEFBQcwAoZQaHR0
// SIG // cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9wcy9jZXJ0
// SIG // cy9NaWNyb3NvZnQlMjBUaW1lLVN0YW1wJTIwUENBJTIw
// SIG // MjAxMCgxKS5jcnQwDAYDVR0TAQH/BAIwADAWBgNVHSUB
// SIG // Af8EDDAKBggrBgEFBQcDCDAOBgNVHQ8BAf8EBAMCB4Aw
// SIG // DQYJKoZIhvcNAQELBQADggIBAGIAz6equnAbb23FJe/j
// SIG // aj4KxN7YLhuhpF8WO70lpaQtMfCrumSc040vef5QbfH8
// SIG // HTzcQpeIVisCa6XsFMcIZdTrf/FGxnbCPdmZHQDh32d/
// SIG // 2xoIlWbiO49UUFqL+iS045gfaP7X7MzvTCg3mieAH+m/
// SIG // LtfwB9jokHhc+9vzRDPt9jl511ufCPODWxmFQ8VttzB5
// SIG // Z4AIg2vOoUrraYx5cqaG258ytqiiAl4ld9ZjfHj+lu5u
// SIG // AQ1Pf6ldPrnbTcI8X2R90oTsYoAhFjLfGQFMO8V3x25+
// SIG // M6kKffycrqoyVW2cGMOFZAbQ8zcT+jEGzlQGsjqkFiSY
// SIG // ge1uOJ8Oq4dP5OFpVXvEdzoiehJzdo3Nfj0kdSBCa68N
// SIG // 0yMuRthd4DT/WrkjFKDZT7JxkE68CLe51k8qEDlXM4ON
// SIG // /+5y7+8W1ethxGSYYo3eO6Norf/IxmLYm7k0QvchJaiv
// SIG // CntGN5mD4kwgrR+iy5WP5gKbmvrgsf8P1AkMCP5d9lo1
// SIG // 4V2/3QrkDRBFEY/+mgH3JMhWMReP+4nOnwvgN3jiwCq6
// SIG // oM6Id2QuDF8ryc+qkJJY9n0b5EI+bzmj1wB/EQ22tK47
// SIG // BynIrPGxEJgIv48rj73yiuK30RUn8sugJ4b6MuWPQpoP
// SIG // hDLqxl7itYyvVutAuixMFk3AWdfE2MicJYF3SLuKzXJN
// SIG // L/ipMIIHcTCCBVmgAwIBAgITMwAAABXF52ueAptJmQAA
// SIG // AAAAFTANBgkqhkiG9w0BAQsFADCBiDELMAkGA1UEBhMC
// SIG // VVMxEzARBgNVBAgTCldhc2hpbmd0b24xEDAOBgNVBAcT
// SIG // B1JlZG1vbmQxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jw
// SIG // b3JhdGlvbjEyMDAGA1UEAxMpTWljcm9zb2Z0IFJvb3Qg
// SIG // Q2VydGlmaWNhdGUgQXV0aG9yaXR5IDIwMTAwHhcNMjEw
// SIG // OTMwMTgyMjI1WhcNMzAwOTMwMTgzMjI1WjB8MQswCQYD
// SIG // VQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4G
// SIG // A1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0
// SIG // IENvcnBvcmF0aW9uMSYwJAYDVQQDEx1NaWNyb3NvZnQg
// SIG // VGltZS1TdGFtcCBQQ0EgMjAxMDCCAiIwDQYJKoZIhvcN
// SIG // AQEBBQADggIPADCCAgoCggIBAOThpkzntHIhC3miy9ck
// SIG // eb0O1YLT/e6cBwfSqWxOdcjKNVf2AX9sSuDivbk+F2Az
// SIG // /1xPx2b3lVNxWuJ+Slr+uDZnhUYjDLWNE893MsAQGOhg
// SIG // fWpSg0S3po5GawcU88V29YZQ3MFEyHFcUTE3oAo4bo3t
// SIG // 1w/YJlN8OWECesSq/XJprx2rrPY2vjUmZNqYO7oaezOt
// SIG // gFt+jBAcnVL+tuhiJdxqD89d9P6OU8/W7IVWTe/dvI2k
// SIG // 45GPsjksUZzpcGkNyjYtcI4xyDUoveO0hyTD4MmPfrVU
// SIG // j9z6BVWYbWg7mka97aSueik3rMvrg0XnRm7KMtXAhjBc
// SIG // TyziYrLNueKNiOSWrAFKu75xqRdbZ2De+JKRHh09/SDP
// SIG // c31BmkZ1zcRfNN0Sidb9pSB9fvzZnkXftnIv231fgLrb
// SIG // qn427DZM9ituqBJR6L8FA6PRc6ZNN3SUHDSCD/AQ8rdH
// SIG // GO2n6Jl8P0zbr17C89XYcz1DTsEzOUyOArxCaC4Q6oRR
// SIG // RuLRvWoYWmEBc8pnol7XKHYC4jMYctenIPDC+hIK12Nv
// SIG // DMk2ZItboKaDIV1fMHSRlJTYuVD5C4lh8zYGNRiER9vc
// SIG // G9H9stQcxWv2XFJRXRLbJbqvUAV6bMURHXLvjflSxIUX
// SIG // k8A8FdsaN8cIFRg/eKtFtvUeh17aj54WcmnGrnu3tz5q
// SIG // 4i6tAgMBAAGjggHdMIIB2TASBgkrBgEEAYI3FQEEBQID
// SIG // AQABMCMGCSsGAQQBgjcVAgQWBBQqp1L+ZMSavoKRPEY1
// SIG // Kc8Q/y8E7jAdBgNVHQ4EFgQUn6cVXQBeYl2D9OXSZacb
// SIG // UzUZ6XIwXAYDVR0gBFUwUzBRBgwrBgEEAYI3TIN9AQEw
// SIG // QTA/BggrBgEFBQcCARYzaHR0cDovL3d3dy5taWNyb3Nv
// SIG // ZnQuY29tL3BraW9wcy9Eb2NzL1JlcG9zaXRvcnkuaHRt
// SIG // MBMGA1UdJQQMMAoGCCsGAQUFBwMIMBkGCSsGAQQBgjcU
// SIG // AgQMHgoAUwB1AGIAQwBBMAsGA1UdDwQEAwIBhjAPBgNV
// SIG // HRMBAf8EBTADAQH/MB8GA1UdIwQYMBaAFNX2VsuP6KJc
// SIG // YmjRPZSQW9fOmhjEMFYGA1UdHwRPME0wS6BJoEeGRWh0
// SIG // dHA6Ly9jcmwubWljcm9zb2Z0LmNvbS9wa2kvY3JsL3By
// SIG // b2R1Y3RzL01pY1Jvb0NlckF1dF8yMDEwLTA2LTIzLmNy
// SIG // bDBaBggrBgEFBQcBAQROMEwwSgYIKwYBBQUHMAKGPmh0
// SIG // dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9wa2kvY2VydHMv
// SIG // TWljUm9vQ2VyQXV0XzIwMTAtMDYtMjMuY3J0MA0GCSqG
// SIG // SIb3DQEBCwUAA4ICAQCdVX38Kq3hLB9nATEkW+Geckv8
// SIG // qW/qXBS2Pk5HZHixBpOXPTEztTnXwnE2P9pkbHzQdTlt
// SIG // uw8x5MKP+2zRoZQYIu7pZmc6U03dmLq2HnjYNi6cqYJW
// SIG // AAOwBb6J6Gngugnue99qb74py27YP0h1AdkY3m2CDPVt
// SIG // I1TkeFN1JFe53Z/zjj3G82jfZfakVqr3lbYoVSfQJL1A
// SIG // oL8ZthISEV09J+BAljis9/kpicO8F7BUhUKz/AyeixmJ
// SIG // 5/ALaoHCgRlCGVJ1ijbCHcNhcy4sa3tuPywJeBTpkbKp
// SIG // W99Jo3QMvOyRgNI95ko+ZjtPu4b6MhrZlvSP9pEB9s7G
// SIG // dP32THJvEKt1MMU0sHrYUP4KWN1APMdUbZ1jdEgssU5H
// SIG // LcEUBHG/ZPkkvnNtyo4JvbMBV0lUZNlz138eW0QBjloZ
// SIG // kWsNn6Qo3GcZKCS6OEuabvshVGtqRRFHqfG3rsjoiV5P
// SIG // ndLQTHa1V1QJsWkBRH58oWFsc/4Ku+xBZj1p/cvBQUl+
// SIG // fpO+y/g75LcVv7TOPqUxUYS8vwLBgqJ7Fx0ViY1w/ue1
// SIG // 0CgaiQuPNtq6TPmb/wrpNPgkNWcr4A245oyZ1uEi6vAn
// SIG // Qj0llOZ0dFtq0Z4+7X6gMTN9vMvpe784cETRkPHIqzqK
// SIG // Oghif9lwY1NNje6CbaUFEMFxBmoQtB1VM1izoXBm8qGC
// SIG // A00wggI1AgEBMIH5oYHRpIHOMIHLMQswCQYDVQQGEwJV
// SIG // UzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMH
// SIG // UmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBv
// SIG // cmF0aW9uMSUwIwYDVQQLExxNaWNyb3NvZnQgQW1lcmlj
// SIG // YSBPcGVyYXRpb25zMScwJQYDVQQLEx5uU2hpZWxkIFRT
// SIG // UyBFU046ODkwMC0wNUUwLUQ5NDcxJTAjBgNVBAMTHE1p
// SIG // Y3Jvc29mdCBUaW1lLVN0YW1wIFNlcnZpY2WiIwoBATAH
// SIG // BgUrDgMCGgMVALvJxdVnHduwOkmSvtW5yCmSyjO4oIGD
// SIG // MIGApH4wfDELMAkGA1UEBhMCVVMxEzARBgNVBAgTCldh
// SIG // c2hpbmd0b24xEDAOBgNVBAcTB1JlZG1vbmQxHjAcBgNV
// SIG // BAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEmMCQGA1UE
// SIG // AxMdTWljcm9zb2Z0IFRpbWUtU3RhbXAgUENBIDIwMTAw
// SIG // DQYJKoZIhvcNAQELBQACBQDuW0EMMCIYDzIwMjYwOTIx
// SIG // MDUzNzE2WhgPMjAyNjA5MjIwNTM3MTZaMHQwOgYKKwYB
// SIG // BAGEWQoEATEsMCowCgIFAO5bQQwCAQAwBwIBAAICBW4w
// SIG // BwIBAAICFDwwCgIFAO5ckowCAQAwNgYKKwYBBAGEWQoE
// SIG // AjEoMCYwDAYKKwYBBAGEWQoDAqAKMAgCAQACAwehIKEK
// SIG // MAgCAQACAwGGoDANBgkqhkiG9w0BAQsFAAOCAQEAuGrH
// SIG // UBRzmFMISWIPAEV8T2uerQWDy6lBJWUlUPjUdDQH8krC
// SIG // 8ssirs9JzjOeXeQh+t7EUmvZqvqxLbcxekPF+GhyhMf6
// SIG // IDQR/+RoE01C8CGco/IM4j4JvLe1Oah+VEP78cuoOtUK
// SIG // KUOpoGgtqoU2bp9wc1ab1+/RF5l4VyoFHdPhpTK8TPmS
// SIG // PoO2b242I3qulZWViv1LE+mOWc8dx/9QJQsexuigoYZR
// SIG // TKRoQqUyY6NONMgkNeCAEDw8XQ4jyw2gy5nQ+qhLuLdt
// SIG // df4zYuuWQ/e7mcmbJrZ84AgcMKvSUaDJybPXp5ZyaeDY
// SIG // HMl6LgLPg5xDYMPm0NtV3SrXF61D1TGCBA0wggQJAgEB
// SIG // MIGTMHwxCzAJBgNVBAYTAlVTMRMwEQYDVQQIEwpXYXNo
// SIG // aW5ndG9uMRAwDgYDVQQHEwdSZWRtb25kMR4wHAYDVQQK
// SIG // ExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xJjAkBgNVBAMT
// SIG // HU1pY3Jvc29mdCBUaW1lLVN0YW1wIFBDQSAyMDEwAhMz
// SIG // AAACIkHS9qr/yLX/AAEAAAIiMA0GCWCGSAFlAwQCAQUA
// SIG // oIIBSjAaBgkqhkiG9w0BCQMxDQYLKoZIhvcNAQkQAQQw
// SIG // LwYJKoZIhvcNAQkEMSIEILL1IP5yc+AbAjrd2WEgpq+d
// SIG // lsBtneQCOTqLwLCLdtCyMIH6BgsqhkiG9w0BCRACLzGB
// SIG // 6jCB5zCB5DCBvQQgBWBdAQoE58aCM2ySYM6ZtwQg6ccY
// SIG // 3AD5BxG58NHkCRMwgZgwgYCkfjB8MQswCQYDVQQGEwJV
// SIG // UzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMH
// SIG // UmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBv
// SIG // cmF0aW9uMSYwJAYDVQQDEx1NaWNyb3NvZnQgVGltZS1T
// SIG // dGFtcCBQQ0EgMjAxMAITMwAAAiJB0vaq/8i1/wABAAAC
// SIG // IjAiBCDwIGnwg3IpUHCSEq3wH7i/op7fp9PxHrng7W/4
// SIG // 92LAxDANBgkqhkiG9w0BAQsFAASCAgBmuRmYjZs7e0yM
// SIG // vnwJ+scerfjdaxwoT9CfLzp82UkvjNMJLL6XCLiIt8G1
// SIG // 0wCFZYA4ViEEJ33WbUwGX1icssaTG69Hr3xqLnTWMEki
// SIG // Q5ISORoROYYm+eNJHbjGZ0QiH8d2sYZs3bz7cV+LHa9P
// SIG // EjkMATHH/GMFkISAMZavhGddI3WTjlwWQpfvRftq8FOQ
// SIG // RRI2IlFzfPv+PKA92tFqFCGsZB0PzjMbFI66svZuycMx
// SIG // Tci3vfFb36In6xe/yy5fT6wBw0pmzOgsyGXBAOtSSC2f
// SIG // nNdiCl67NKHFx8cPm5xZlkOVSquxipU4vm385QiyZYau
// SIG // +A/SxijGFpYqky9JBJpZw00h8t7hePtWwa72YaXz5nmz
// SIG // jYHCtCgIXNLN9hHXA0fJn7rUeM1usy7cyuB8bfcCAhJe
// SIG // MPxmspd8uhdVPVZ3Nbz+Tfd7xNQiO+zf/KNdlEDt7PTh
// SIG // lckm/TJG5EooJturymqDQXc8myi/UgPCUIJGZt36ApWz
// SIG // /fLkthHnfkoo5z2L0z7Q6/Hs5vdT57eeconvO2rL76I+
// SIG // qinesOcH+ML8wOtU4YD8iyibhzFnfkhQA6NLhWbHBqq9
// SIG // 0jJhuLUA+vOvh9JLCBg90OottMj2zQi2kMlaT2JrynbZ
// SIG // HAXTJM0iwHYg5Obwp+cJwd/gvKId6/qIF2U7AWjPWdbp
// SIG // MQknvvbafQ==
// SIG // End signature block
