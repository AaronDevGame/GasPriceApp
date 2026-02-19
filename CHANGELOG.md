# Changelog

All notable changes to this project will be documented in this file.

---

## [1.1.0] - 2026-02-13

### Added
- Admin authentication middleware
- Rate limiting middleware
- Custom 404 fallback response
- Structured ApiResponse wrapper
- InstanceId in all responses
- ErrorCodes class

### Improved
- Response consistency across endpoints
- Server state handling

### Fixed
- Nullable warnings in ApiResponse
- Minor status logic adjustments

## [1.2.0] - 2026-02-14

### Added
- Start implement versioning
- 404 rate limiting
- Changelog file

## [1.2.1] - 2026-02-18

### Fixed
-  Prevent unbounded dictionary growth in rate limit middleware

## [1.2.2] - 2026-02-18

### Added
-  Health check endpoint

## [1.2.3] - 2026-02-19

### Added
-  Include health, info, and routes in public routes