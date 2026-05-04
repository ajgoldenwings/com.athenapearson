using Amazon.CDK;
using Amazon.CDK.AWS.CertificateManager;
using Amazon.CDK.AWS.CloudFront;
using Amazon.CDK.AWS.CloudFront.Origins;
using Amazon.CDK.AWS.Route53;
using Amazon.CDK.AWS.Route53.Targets;
using Amazon.CDK.AWS.S3;
using Constructs;
using System.Linq;

namespace Infrastructure.Constructs
{
    internal class DistributionConstructProps : IStackProps
    {
        public Bucket Bucket;
        public string DomainName;
    }

    public class DistributionConstruct : Construct
    {
        public Distribution distribution;

        internal DistributionConstruct(Construct scope, string id, DistributionConstructProps props = null) : base(scope, id)
        {
            var domainName = props.DomainName;
            var wwwDomainName = $"www.{domainName}";

            var hostedZone = HostedZone.FromLookup(
                this, "hostedZone",
                new HostedZoneProviderProps
                {
                    DomainName = string.Join(".", domainName.Split(".").Reverse().Take(2).Reverse().ToArray())
                });

            var certificate = new Certificate(
                this, "certificate",
                new CertificateProps
                {
                    DomainName = domainName,
                    SubjectAlternativeNames = [wwwDomainName],
                    Validation = CertificateValidation.FromDns(hostedZone)
                });

            var s3BucketOrigin = S3BucketOrigin.WithOriginAccessControl(props.Bucket);

            // Create a Response Headers Policy with Content-Security-Policy
            var responseHeadersPolicy = new ResponseHeadersPolicy(
                this, "responseHeadersPolicy",
                new ResponseHeadersPolicyProps
                {
                    ResponseHeadersPolicyName = $"{domainName.Replace(".", "-")}-security-headers",
                    SecurityHeadersBehavior = new ResponseSecurityHeadersBehavior
                    {
                        ContentSecurityPolicy = new ResponseHeadersContentSecurityPolicy
                        {
                            ContentSecurityPolicy = "default-src 'self'; script-src 'self' 'unsafe-inline' https://www.googletagmanager.com https://www.google-analytics.com; connect-src 'self' https://cognito-idp.us-east-1.amazonaws.com https://www.google-analytics.com https://www.googletagmanager.com; img-src 'self' data: https:; style-src 'self' 'unsafe-inline'; font-src 'self' data:; frame-src https://www.youtube.com; frame-ancestors 'none'; base-uri 'self'; form-action 'self';",
                            Override = true
                        },
                        StrictTransportSecurity = new ResponseHeadersStrictTransportSecurity
                        {
                            AccessControlMaxAge = Duration.Seconds(63072000),
                            IncludeSubdomains = true,
                            Override = true
                        },
                        ContentTypeOptions = new ResponseHeadersContentTypeOptions
                        {
                            Override = true
                        },
                        ReferrerPolicy = new ResponseHeadersReferrerPolicy
                        {
                            ReferrerPolicy = HeadersReferrerPolicy.STRICT_ORIGIN_WHEN_CROSS_ORIGIN,
                            Override = true
                        },
                        XssProtection = new ResponseHeadersXSSProtection
                        {
                            Protection = true,
                            ModeBlock = true,
                            Override = true
                        },
                        FrameOptions = new ResponseHeadersFrameOptions
                        {
                            FrameOption = HeadersFrameOption.DENY,
                            Override = true
                        }
                    }
                });

            distribution = new Distribution(
                this, "distribution",
                new DistributionProps
                {
                    DomainNames = [domainName, wwwDomainName],
                    DefaultBehavior = new BehaviorOptions
                    {
                        Origin = s3BucketOrigin,
                        Compress = true,
                        AllowedMethods = AllowedMethods.ALLOW_GET_HEAD,
                        CachedMethods = CachedMethods.CACHE_GET_HEAD,
                        ViewerProtocolPolicy = ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
                        CachePolicy = CachePolicy.CACHING_OPTIMIZED,
                        ResponseHeadersPolicy = responseHeadersPolicy
                    },
                    AdditionalBehaviors = new Dictionary<string, IBehaviorOptions>
                    {
                        // Cache static assets (JS, CSS, images) for longer periods
                        ["/assets/*"] = new BehaviorOptions
                        {
                            Origin = s3BucketOrigin,
                            Compress = true,
                            AllowedMethods = AllowedMethods.ALLOW_GET_HEAD,
                            CachedMethods = CachedMethods.CACHE_GET_HEAD,
                            ViewerProtocolPolicy = ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
                            CachePolicy = CachePolicy.CACHING_OPTIMIZED,
                            ResponseHeadersPolicy = responseHeadersPolicy
                        },
                        // Don't cache index.html to ensure SPA routing works correctly
                        ["/index.html"] = new BehaviorOptions
                        {
                            Origin = s3BucketOrigin,
                            Compress = true,
                            AllowedMethods = AllowedMethods.ALLOW_GET_HEAD,
                            CachedMethods = CachedMethods.CACHE_GET_HEAD,
                            ViewerProtocolPolicy = ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
                            CachePolicy = CachePolicy.CACHING_DISABLED,
                            ResponseHeadersPolicy = responseHeadersPolicy
                        }
                    },
                    ErrorResponses =
                    [
                        new ErrorResponse { HttpStatus = 403, ResponsePagePath = "/index.html", ResponseHttpStatus = 403, Ttl = Duration.Minutes(0) },
                        new ErrorResponse { HttpStatus = 404, ResponsePagePath = "/index.html", ResponseHttpStatus = 404, Ttl = Duration.Minutes(0) }
                    ],
                    PriceClass = PriceClass.PRICE_CLASS_100, // USA, Canada, Europe, & Israel
                    Enabled = true,
                    Certificate = certificate,
                    MinimumProtocolVersion = SecurityPolicyProtocol.TLS_V1_2_2021,
                    HttpVersion = HttpVersion.HTTP2,
                    DefaultRootObject = "index.html",
                    EnableIpv6 = true
                });

            var route53ARecord = new ARecord(
                this, "route53ARecord",
                new ARecordProps
                {
                    RecordName = domainName,
                    Zone = hostedZone,
                    Target = RecordTarget.FromAlias(new CloudFrontTarget(distribution))
                });

            _ = new ARecord(
                this, "wwwRoute53ARecord",
                new ARecordProps
                {
                    RecordName = wwwDomainName,
                    Zone = hostedZone,
                    Target = RecordTarget.FromAlias(new Route53RecordTarget(route53ARecord))
                });
        }
    }
}
