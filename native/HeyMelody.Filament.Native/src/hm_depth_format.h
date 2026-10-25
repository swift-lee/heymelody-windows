#pragma once

namespace hm {

enum class DepthFormat { Depth24, Depth32F, Unsupported };

struct DepthFormatSelection {
    DepthFormat format;
    bool depth24_supported;
    bool depth32f_supported;
};

template<typename Query>
DepthFormatSelection select_depth_format(Query&& query) {
    const bool depth24_supported = query(DepthFormat::Depth24);
    const bool depth32f_supported = query(DepthFormat::Depth32F);
    return {
        depth24_supported ? DepthFormat::Depth24
            : depth32f_supported ? DepthFormat::Depth32F : DepthFormat::Unsupported,
        depth24_supported,
        depth32f_supported
    };
}

} // namespace hm
