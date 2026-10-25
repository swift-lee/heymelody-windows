#include "hm_depth_format.h"

#include <array>
#include <iostream>
#include <stdexcept>
#include <string>

namespace {
void check_case(const char* label, bool depth24, bool depth32f, hm::DepthFormat expected) {
    std::array<hm::DepthFormat, 2> queries{};
    std::size_t query_count = 0;
    const auto selected = hm::select_depth_format([&](hm::DepthFormat format) {
        if (query_count == queries.size()) throw std::runtime_error("Too many depth format queries");
        queries[query_count++] = format;
        if (format == hm::DepthFormat::Depth24) return depth24;
        if (format == hm::DepthFormat::Depth32F) return depth32f;
        throw std::runtime_error("Unexpected depth format query");
    });
    if (selected.format != expected || selected.depth24_supported != depth24
            || selected.depth32f_supported != depth32f) {
        throw std::runtime_error(std::string(label) + ": incorrect format or reported capabilities");
    }
    if (query_count != 2 || queries[0] != hm::DepthFormat::Depth24
            || queries[1] != hm::DepthFormat::Depth32F) {
        throw std::runtime_error(std::string(label) + ": expected exactly one Depth24 then Depth32F query");
    }
}

struct FakeEngine {
    bool depth24;
    bool depth32f;
    unsigned query_count = 0;

    hm::DepthFormatSelection select() {
        return hm::select_depth_format([this](hm::DepthFormat format) {
            ++query_count;
            return format == hm::DepthFormat::Depth24 ? depth24 : depth32f;
        });
    }
};
}

int main() {
    try {
        check_case("both supported", true, true, hm::DepthFormat::Depth24);
        check_case("Depth24 only", true, false, hm::DepthFormat::Depth24);
        check_case("Depth32F only", false, true, hm::DepthFormat::Depth32F);
        check_case("neither supported", false, false, hm::DepthFormat::Unsupported);

        FakeEngine first{true, false};
        FakeEngine second{false, true};
        const auto first_selection = first.select();
        const auto second_selection = second.select();
        const auto repeated_first_selection = first.select();
        if (first_selection.format != hm::DepthFormat::Depth24
                || second_selection.format != hm::DepthFormat::Depth32F
                || repeated_first_selection.format != hm::DepthFormat::Depth24
                || first.query_count != 4 || second.query_count != 2) {
            throw std::runtime_error("Depth selection must query each Engine independently without caching");
        }

        std::cout << "Depth format: 4 capability combinations and independent Engine queries passed\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
