<template>
  <section class="page network-page">
    <!-- 页面头部：标题、说明和手动刷新按钮 -->
    <PageHeader
      title="网络"
      description="查看一段时间内的流量汇总、占比和应用排行。"
    >
      <template #actions>
        <button class="ghost-button" :disabled="isLoading" @click="refreshApps">
          <span class="button-inline-icon"><AppIcon name="refresh" :size="14" /></span>
          {{ isLoading ? '刷新中...' : '刷新' }}
        </button>
      </template>
    </PageHeader>

    <!-- 查询条件区：按工具栏分组展示时间、范围、方向和 TopN -->
    <article class="card filters-card filters-card-elevated network-section network-section-filters">
      <div id="network-filters-toolbar" v-show="showAdvancedFilters" class="filters-toolbar">
        <section class="filter-group filter-group-time">
          <div class="filter-group-title">
            <strong>时间</strong>
            <small>选择统计区间</small>
          </div>
          <div class="filter-group-fields filter-group-fields-time">
            <label class="filter-field">
              <span>开始时间</span>
              <input v-model="filters.from" type="datetime-local" />
            </label>
            <label class="filter-field">
              <span>结束时间</span>
              <input v-model="filters.to" type="datetime-local" />
            </label>
          </div>
        </section>

        <section class="filter-group">
          <div class="filter-group-title">
            <strong>范围</strong>
            <small>选择网络范围</small>
          </div>
          <div class="filter-group-fields">
            <label class="filter-field">
              <span>统计范围</span>
              <select v-model="filters.scope">
                <option value="all">全部</option>
                <option value="wan">WAN</option>
                <option value="lan">LAN</option>
                <option value="loopback">Loopback</option>
              </select>
            </label>
          </div>
        </section>

        <section class="filter-group">
          <div class="filter-group-title">
            <strong>方向</strong>
            <small>选择统计方向</small>
          </div>
          <div class="filter-group-fields">
            <label class="filter-field">
              <span>统计方向</span>
              <select v-model="filters.direction">
                <option value="total">总流量</option>
                <option value="upload">上传</option>
                <option value="download">下载</option>
              </select>
            </label>
          </div>
        </section>

        <section class="filter-group filter-group-topn">
          <div class="filter-group-title">
            <strong>TopN</strong>
            <small>控制排行数量</small>
          </div>
          <div class="filter-group-fields">
            <label class="filter-field">
              <span>排行数量</span>
              <input v-model.number="filters.topN" type="number" min="1" max="100" />
            </label>
          </div>
        </section>
      </div>

      <div class="preset-toolbar">
        <div class="preset-toolbar-title">
          <strong>常用预设</strong>
          <small>快速切换常见时间范围</small>
        </div>
        <div class="preset-row">
          <button
            v-for="preset in presetOptions"
            :key="preset.hours"
            class="chip-button"
            :class="{ 'chip-button-active': activePresetHours === preset.hours }"
            @click="applyPreset(preset.hours)"
          >
            {{ preset.label }}
          </button>
        </div>
      </div>

      <button
        v-if="isMobileViewport"
        type="button"
        class="ghost-button network-mobile-toggle"
        :class="{ 'network-mobile-toggle-active': isMobileFiltersExpanded }"
        :aria-expanded="isMobileFiltersExpanded"
        aria-controls="network-filters-toolbar"
        @click="toggleMobileFilters"
      >
        <span>{{ isMobileFiltersExpanded ? '收起更多筛选' : '展开更多筛选' }}</span>
        <span class="network-mobile-toggle-icon" aria-hidden="true">{{ isMobileFiltersExpanded ? '▴' : '▾' }}</span>
      </button>
    </article>

    <div v-if="errorMessage" class="card state-card error-state" role="alert">
      {{ errorMessage }}
    </div>
    <div v-if="settingsDefaultWarning" class="card state-card warning-state" role="status">
      {{ settingsDefaultWarning }}
    </div>

    <section class="panel-grid network-section network-section-analytics" :aria-busy="isLoading">
      <div class="network-section network-section-column network-section-column-analytics">
        <article class="card dashboard-panel-card network-section network-section-upload-download">
          <div class="panel-header">
            <div class="panel-title">
              <span class="panel-icon"><AppIcon name="traffic" :size="16" /></span>
              <div>
                <h3>累计上传 / 下载</h3>
                <p class="panel-subtitle">基于累计总流量展示上传与下载方向的整体分布。</p>
              </div>
            </div>
            <span class="section-tag">仅基于累计总量</span>
          </div>

          <div class="ratio-overview">
            <div class="ratio-summary-card ratio-summary-card-wan">
              <div class="ratio-summary-top">
                <span class="ratio-summary-label">累计上传</span>
                <span class="ratio-summary-badge">发送</span>
              </div>
              <strong>{{ formatBytes(totalUploadBytes) }}</strong>
              <small>{{ formatRatioPercent(uploadDownloadTotalBytes, uploadPercent) }}</small>
            </div>

            <div class="ratio-summary-card ratio-summary-card-lan">
              <div class="ratio-summary-top">
                <span class="ratio-summary-label">累计下载</span>
                <span class="ratio-summary-badge">接收</span>
              </div>
              <strong>{{ formatBytes(totalDownloadBytes) }}</strong>
              <small>{{ formatRatioPercent(uploadDownloadTotalBytes, downloadPercent) }}</small>
            </div>

            <div class="ratio-summary-card ratio-summary-card-neutral">
              <div class="ratio-summary-top">
                <span class="ratio-summary-label">累计总流量</span>
                <span class="ratio-summary-badge">总计</span>
              </div>
              <strong>{{ formatBytes(uploadDownloadTotalBytes) }}</strong>
              <small>上传 + 下载</small>
            </div>

            <div class="ratio-summary-card ratio-summary-card-neutral">
              <div class="ratio-summary-top">
                <span class="ratio-summary-label">主导方向</span>
                <span class="ratio-summary-badge">概览</span>
              </div>
              <strong>{{ dominantTrafficDirectionLabel }}</strong>
              <small>{{ dominantTrafficDirectionHint }}</small>
            </div>
          </div>

          <div class="ratio-group">
            <div class="ratio-item">
              <div class="ratio-header">
                <strong>上传</strong>
                <span>{{ formatRatioPercent(uploadDownloadTotalBytes, uploadPercent) }}</span>
              </div>
              <div class="ratio-track">
                <div class="ratio-bar ratio-bar-wan" :style="{ width: `${uploadPercent}%` }"></div>
              </div>
              <small class="muted">{{ formatBytes(totalUploadBytes) }}</small>
            </div>

            <div class="ratio-item">
              <div class="ratio-header">
                <strong>下载</strong>
                <span>{{ formatRatioPercent(uploadDownloadTotalBytes, downloadPercent) }}</span>
              </div>
              <div class="ratio-track">
                <div class="ratio-bar ratio-bar-lan" :style="{ width: `${downloadPercent}%` }"></div>
              </div>
              <small class="muted">{{ formatBytes(totalDownloadBytes) }}</small>
            </div>
          </div>
        </article>

        <!-- 占比面板：展示 WAN / LAN / Loopback 在当前时间范围和方向下的占比 -->
        <article class="card dashboard-panel-card network-ratio-panel network-section network-section-wan-lan">
          <div class="panel-header">
            <div class="panel-title">
              <span class="panel-icon"><AppIcon name="traffic" :size="16" /></span>
              <div>
                <h3>WAN / LAN 占比</h3>
                <p class="panel-subtitle">按当前筛选结果汇总的累计流量分布，包含其他未分类流量。</p>
              </div>
            </div>
            <span class="section-tag">按当前筛选结果汇总</span>
          </div>

          <div class="ratio-overview">
            <div class="ratio-summary-card ratio-summary-card-wan">
              <div class="ratio-summary-top">
                <span class="ratio-summary-label">WAN 流量</span>
                <span class="ratio-summary-badge">外网</span>
              </div>
              <strong>{{ formatBytes(wanTotalBytes) }}</strong>
              <small>{{ formatRatioPercent(overviewTotalBytes, wanPercent) }}</small>
            </div>

            <div class="ratio-summary-card ratio-summary-card-lan">
              <div class="ratio-summary-top">
                <span class="ratio-summary-label">LAN 流量</span>
                <span class="ratio-summary-badge">内网</span>
              </div>
              <strong>{{ formatBytes(lanTotalBytes) }}</strong>
              <small>{{ formatRatioPercent(overviewTotalBytes, lanPercent) }}</small>
            </div>

            <div class="ratio-summary-card ratio-summary-card-loopback">
              <div class="ratio-summary-top">
                <span class="ratio-summary-label">Loopback 流量</span>
                <span class="ratio-summary-badge">本地</span>
              </div>
              <strong>{{ formatBytes(loopbackTotalBytes) }}</strong>
              <small>{{ formatRatioPercent(overviewTotalBytes, loopbackPercent) }}</small>
            </div>

            <div class="ratio-summary-card ratio-summary-card-other">
              <div class="ratio-summary-top">
                <span class="ratio-summary-label">其他流量</span>
                <span class="ratio-summary-badge">未分类</span>
              </div>
              <strong>{{ formatBytes(otherTotalBytes) }}</strong>
              <small>{{ formatRatioPercent(overviewTotalBytes, otherPercent) }}</small>
            </div>

            <div class="ratio-summary-card ratio-summary-card-neutral">
              <div class="ratio-summary-top">
                <span class="ratio-summary-label">主导网络</span>
                <span class="ratio-summary-badge">概览</span>
              </div>
              <strong>{{ dominantScopeLabel }}</strong>
              <small>{{ dominantScopeHint }}</small>
            </div>
          </div>

          <div class="ratio-group">
            <div class="ratio-item">
              <div class="ratio-header">
                <strong>WAN</strong>
                <span>{{ formatRatioPercent(overviewTotalBytes, wanPercent) }}</span>
              </div>
              <div class="ratio-track">
                <div class="ratio-bar ratio-bar-wan" :style="{ width: `${wanPercent}%` }"></div>
              </div>
              <small class="muted">{{ formatBytes(wanTotalBytes) }}</small>
            </div>

            <div class="ratio-item">
              <div class="ratio-header">
                <strong>LAN</strong>
                <span>{{ formatRatioPercent(overviewTotalBytes, lanPercent) }}</span>
              </div>
              <div class="ratio-track">
                <div class="ratio-bar ratio-bar-lan" :style="{ width: `${lanPercent}%` }"></div>
              </div>
              <small class="muted">{{ formatBytes(lanTotalBytes) }}</small>
            </div>

            <div class="ratio-item">
              <div class="ratio-header">
                <strong>Loopback</strong>
                <span>{{ formatRatioPercent(overviewTotalBytes, loopbackPercent) }}</span>
              </div>
              <div class="ratio-track">
                <div class="ratio-bar ratio-bar-loopback" :style="{ width: `${loopbackPercent}%` }"></div>
              </div>
              <small class="muted">{{ formatBytes(loopbackTotalBytes) }}</small>
            </div>

            <div class="ratio-item">
              <div class="ratio-header">
                <strong>其他</strong>
                <span>{{ formatRatioPercent(overviewTotalBytes, otherPercent) }}</span>
              </div>
              <div class="ratio-track">
                <div class="ratio-bar ratio-bar-other" :style="{ width: `${otherPercent}%` }"></div>
              </div>
              <small class="muted">{{ formatBytes(otherTotalBytes) }}</small>
            </div>
          </div>
        </article>
      </div>

      <div class="network-section network-section-column network-section-column-ranking">
        <!-- 排行面板：展示按当前 scope + direction 排序后的应用流量排行 -->
        <article class="card dashboard-panel-card network-ranking-panel network-section network-section-ranking">
          <div class="panel-header">
            <div class="panel-title">
              <span class="panel-icon"><AppIcon name="apps" :size="16" /></span>
              <div>
                <h3>应用流量排行</h3>
                <p class="panel-subtitle">按选择的统计范围和方向排序显示应用流量排行。</p>
              </div>
            </div>
            <div class="ranking-header-tags">
              <button
                type="button"
                class="section-tag ranking-ignored-count"
                :class="{ 'ranking-ignored-count-active': ignoredApps.length > 0 }"
                :disabled="ignoredApps.length === 0"
                @click="scrollToIgnoredApps"
              >
                <AppIcon name="eye-off" :size="13" />
                已忽略 {{ ignoredApps.length }}
              </button>
              <span class="section-tag">{{ rankingDescription }}</span>
            </div>
          </div>

          <div v-if="hasPendingFilterChanges" class="network-pending-state" role="status">
            筛选条件已修改，正在等待最新结果；当前排行仍对应 {{ rankingDescription }}。
          </div>

          <div class="ranking-meta">
            <div class="card metric-card metric-card-compact network-stat-card">
              <div class="metric-top">
                <span class="metric-label metric-label-inline"><AppIcon name="apps" :size="14" />应用数量</span>
              </div>
              <strong class="metric-value">{{ topRanking.length }}</strong>
            </div>
            <div class="card metric-card metric-card-compact network-stat-card">
              <div class="metric-top">
                <span class="metric-label metric-label-inline"><AppIcon name="uptime" :size="14" />统计时间范围</span>
              </div>
              <div v-if="rangeParts" class="metric-value metric-small range-value">
                <span>{{ rangeParts.from }}&nbsp;~</span>
                <span>{{ rangeParts.to }}</span>
              </div>
              <strong v-else class="metric-value metric-small">--</strong>
            </div>
          </div>

          <div class="ranking-table-head" aria-hidden="true">
            <span>排名</span>
            <span>应用名称</span>
            <span>应用路径</span>
            <span>上传 / 下载</span>
            <span>当前统计</span>
            <span>操作</span>
          </div>

          <ol v-if="showRankingSkeleton" class="ranking-list ranking-list-skeleton" aria-hidden="true">
            <li v-for="placeholder in rankingSkeletonRows" :key="placeholder" class="ranking-item ranking-item-skeleton">
              <div class="ranking-main">
                <span class="ranking-index ranking-skeleton-badge"></span>
                <span class="ranking-skeleton-block ranking-skeleton-title"></span>
                <span class="ranking-skeleton-block ranking-skeleton-subtitle"></span>
              </div>
              <div class="ranking-side">
                <span class="ranking-skeleton-block ranking-skeleton-value"></span>
                <div class="ranking-breakdown">
                  <span class="ranking-skeleton-block ranking-skeleton-flow"></span>
                  <span class="ranking-skeleton-block ranking-skeleton-flow"></span>
                </div>
                <div class="ranking-progress ranking-progress-skeleton">
                  <div class="ranking-progress-bar ranking-progress-bar-skeleton"></div>
                </div>
              </div>
            </li>
          </ol>

          <ol v-else class="ranking-list">
            <li
              v-for="(item, index) in topRanking"
              :key="item.appKey"
              class="ranking-item"
              :class="[
                index < 3 ? [`ranking-item-top`, `ranking-item-top-${index + 1}`] : [],
                { 'ranking-item-active': selectedApp?.appKey === item.appKey }
              ]"
            >
              <button
                type="button"
                class="ranking-open-button"
                :aria-label="`查看 ${item.displayName || item.processName} 的流量明细`"
                :aria-pressed="selectedApp?.appKey === item.appKey"
                @click="selectApp(item)"
              >
                <div class="ranking-main">
                  <span class="ranking-index">{{ index + 1 }}</span>
                  <strong>{{ item.displayName || item.processName }}</strong>
                  <small class="muted">{{ item.executablePath || item.processName }}</small>
                </div>
                <div class="ranking-side">
                  <span class="ranking-value">{{ formatBytes(getRankingValue(item)) }}</span>
                  <div class="ranking-breakdown">
                    <span class="ranking-flow">
                      <AppIcon name="upload" :size="12" />
                      {{ formatBytes(getScopedUploadBytes(item)) }}
                    </span>
                    <span class="ranking-flow">
                      <AppIcon name="download" :size="12" />
                      {{ formatBytes(getScopedDownloadBytes(item)) }}
                    </span>
                  </div>
                  <div class="ranking-progress" aria-hidden="true">
                    <div class="ranking-progress-bar" :style="{ width: `${getRankingPercent(item)}%` }"></div>
                  </div>
                </div>
              </button>
              <button
                type="button"
                class="ranking-ignore-button"
                :aria-label="`忽略 ${item.displayName || item.processName}，不再参与排行`"
                title="忽略此应用"
                :disabled="isIgnoreMutationPending"
                @click.stop="ignoreApp(item)"
              >
                <AppIcon name="eye-off" :size="17" />
              </button>
            </li>
            <li v-if="!topRanking.length" class="network-empty-state">
              <strong>当前条件没有可展示的排行数据</strong>
              <small>{{ appliedFilters ? `查询范围：${rankingDescription}，${formatRangeLabel()}` : '等待第一次成功查询。' }}</small>
              <div class="network-empty-actions">
                <button v-if="selectedPresetHours !== 24" type="button" class="chip-button" @click="applyPreset(24)">切换最近 24 小时</button>
                <button type="button" class="chip-button" @click="focusNetworkFilters">调整筛选</button>
                <button v-if="ignoredApps.length" type="button" class="chip-button" @click="scrollToIgnoredApps">查看已忽略</button>
              </div>
            </li>
          </ol>

          <section
            v-if="ignoredApps.length"
            ref="ignoredAppsPanel"
            class="ranking-ignored-panel"
            aria-labelledby="ranking-ignored-title"
          >
            <button
              type="button"
              class="ranking-ignored-heading"
              :aria-expanded="isIgnoredAppsExpanded"
              aria-controls="ranking-ignored-list"
              @click="isIgnoredAppsExpanded = !isIgnoredAppsExpanded"
            >
              <span class="ranking-ignored-heading-main">
                <span class="ranking-ignored-icon"><AppIcon name="eye-off" :size="16" /></span>
                <span>
                  <strong id="ranking-ignored-title">已忽略的应用</strong>
                  <small>这些应用不会参与排行，流量统计原始数据保持不变。</small>
                </span>
              </span>
              <span class="ranking-ignored-heading-action">
                {{ isIgnoredAppsExpanded ? '收起' : '展开' }}
                <span class="ranking-ignored-count-badge">{{ ignoredApps.length }}</span>
              </span>
            </button>

            <ul v-show="isIgnoredAppsExpanded" id="ranking-ignored-list" class="ranking-ignored-list">
              <li v-for="entry in ignoredAppRows" :key="entry.record.appKey" class="ranking-ignored-item">
                <div class="ranking-ignored-app">
                  <strong>{{ entry.record.displayName || entry.record.processName }}</strong>
                  <small>{{ entry.record.executablePath || entry.record.processName }}</small>
                </div>
                <div v-if="entry.summary" class="ranking-ignored-traffic">
                  <span>
                    <AppIcon name="upload" :size="12" />
                    {{ formatBytes(getScopedUploadBytes(entry.summary)) }}
                  </span>
                  <span>
                    <AppIcon name="download" :size="12" />
                    {{ formatBytes(getScopedDownloadBytes(entry.summary)) }}
                  </span>
                </div>
                <span v-else class="ranking-ignored-unavailable">当前范围无排行数据</span>
                <button
                  type="button"
                  class="ghost-button ranking-restore-button"
                  :disabled="isIgnoreMutationPending"
                  @click="restoreIgnoredApp(entry.record.appKey)"
                >
                  <AppIcon name="restore" :size="14" />
                  恢复排行
                </button>
              </li>
            </ul>
          </section>
        </article>
      </div>
    </section>

    <Teleport to="body">
      <Transition name="ranking-ignore-toast">
        <div v-if="recentlyIgnoredApp" class="ranking-ignore-toast" role="status" aria-live="polite">
          <span class="ranking-ignore-toast-icon"><AppIcon name="eye-off" :size="17" /></span>
          <span>
            已忽略
            <strong>{{ recentlyIgnoredApp.displayName || recentlyIgnoredApp.processName }}</strong>
          </span>
          <button type="button" :disabled="isIgnoreMutationPending" @click="undoLastIgnore">撤销</button>
        </div>
      </Transition>
    </Teleport>

    <AccessibleModal
      v-if="selectedApp"
      class="app-segments-overlay"
      role="dialog"
      :open="Boolean(selectedApp)"
      title="应用流量明细"
      description="以下明细按当前时间、范围和方向筛选条件查询。"
      :return-focus="dialogTrigger"
      @close="closeAppSegmentsPanel"
    >
        <section
          class="app-segments-panel"
          @click.self="closeAppSegmentsPanel"
        >
          <button type="button" class="app-segments-close" aria-label="关闭应用流量明细" @click="closeAppSegmentsPanel">
            <AppIcon name="close" :size="16" />
          </button>

          <div class="app-segments-header">
            <div class="app-segments-title">
              <span class="panel-icon"><AppIcon name="network" :size="16" /></span>
              <div>
                <h4 id="app-segments-title">{{ selectedApp.displayName || selectedApp.processName }}</h4>
                <p class="panel-subtitle">{{ selectedApp.executablePath || selectedApp.processName }}</p>
              </div>
            </div>
            <div class="app-segments-actions">
              <div class="app-segments-tags">
                <span class="section-tag">{{ segmentDurationLabel }}</span>
                <span class="section-tag">{{ appSegments.length }} 段</span>
              </div>
            </div>
          </div>

          <p class="app-segments-note" :class="{ 'app-segments-note-empty': !selectedAppOutsideRanking }">
            {{ selectedAppOutsideRanking ? '该应用不在当前排行范围内，详情仍按当前筛选条件查询。' : '以下明细按当前时间、范围和方向筛选条件查询。' }}
          </p>

          <div v-if="segmentsErrorMessage" class="app-segments-state app-segments-error">
            {{ segmentsErrorMessage }}
          </div>
          <div v-else-if="isSegmentsLoading" class="app-segments-state">
            正在加载应用分段流量...
          </div>
          <div v-else-if="!appSegments.length" class="app-segments-state">
            当前筛选范围内没有该应用的分段流量。
          </div>
          <div v-else class="app-segments-chart" aria-label="应用分段流量柱状图">
            <div class="app-segments-chart-grid" aria-hidden="true"></div>
            <div class="app-segments-scale" aria-hidden="true">
              <span>{{ formatBytes(segmentMaxValue) }}</span>
              <span>{{ formatBytes(segmentMaxValue / 2) }}</span>
              <span>0 B</span>
            </div>
            <ol class="app-segments-bars">
              <li
                v-for="(segment, segmentIndex) in appSegments"
                :key="`${segment.from}-${segment.to}`"
                class="app-segment-bar-item"
                :class="getSegmentTooltipPlacementClass(segmentIndex)"
              >
                <div
                  class="app-segment-bar"
                  :style="{ height: `${getSegmentPercent(segment)}%` }"
                  tabindex="0"
                  role="button"
                  :aria-pressed="selectedSegmentIndex === segmentIndex"
                  :aria-label="getSegmentTooltip(segment)"
                  @click="selectSegment(segmentIndex)"
                  @keydown="handleSegmentKeydown($event, segmentIndex)"
                >
                  <span class="app-segment-tooltip">{{ getSegmentTooltip(segment) }}</span>
                </div>
                <span class="app-segment-axis-label">{{ formatSegmentAxisLabel(segment) }}</span>
              </li>
            </ol>
          </div>

          <div v-if="selectedSegment" class="app-segments-selected-readout" role="status" aria-live="polite">
            <strong>已选分段</strong>
            <span>{{ formatSegmentRange(selectedSegment) }}</span>
            <span>当前统计 {{ formatBytes(getSegmentValue(selectedSegment)) }}</span>
            <span>上传 {{ formatBytes(getSegmentUploadBytes(selectedSegment)) }}</span>
            <span>下载 {{ formatBytes(getSegmentDownloadBytes(selectedSegment)) }}</span>
          </div>

          <details v-if="appSegments.length" class="app-segments-table-details">
            <summary>展开分段明细表</summary>
            <div class="table-shell">
              <table class="data-table app-segments-table">
                <thead>
                  <tr>
                    <th>时间段</th>
                    <th class="align-right">上传</th>
                    <th class="align-right">下载</th>
                    <th class="align-right">当前统计</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="(segment, segmentIndex) in appSegments" :key="`detail-${segment.from}-${segment.to}`">
                    <td>{{ formatSegmentRange(segment) }}</td>
                    <td class="align-right">{{ formatBytes(getSegmentUploadBytes(segment)) }}</td>
                    <td class="align-right">{{ formatBytes(getSegmentDownloadBytes(segment)) }}</td>
                    <td class="align-right">
                      <button type="button" class="table-inline-action" @click="selectSegment(segmentIndex)">
                        {{ formatBytes(getSegmentValue(segment)) }}
                      </button>
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>
          </details>
        </section>
    </AccessibleModal>
  </section>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch } from 'vue';
import AppIcon from '../components/AppIcon.vue';
import AccessibleModal from '../components/AccessibleModal.vue';
import PageHeader from '../components/PageHeader.vue';
import {
  getNetworkAppSegments,
  getNetworkDashboard,
  getSettings,
  ignoreNetworkApp,
  restoreNetworkApp
} from '../services/api';
import type {
  AppTrafficSegmentDto,
  AppTrafficSummaryDto,
  IgnoredNetworkAppDto,
  NetworkPeriodSummaryDto
} from '../types/monitor';

// 常用时间预设，对应页面顶部的快捷时间按钮
const presetOptions = [
  { hours: 1, label: '最近 1 小时' },
  { hours: 6, label: '最近 6 小时' },
  { hours: 12, label: '最近 12 小时' },
  { hours: 24, label: '最近 24 小时' },
  { hours: 72, label: '最近 3 天' },
  { hours: 24 * 7, label: '最近 1 周' },
  { hours: 24 * 30, label: '最近 30 天' }
] as const;

const ignoredAppsStorageKey = 'monitor.network.ignored-apps.v1';

// 页面主数据：应用排行、汇总卡片、占比面板、加载状态
const items = ref<AppTrafficSummaryDto[]>([]);
const ignoredApps = ref<IgnoredNetworkAppDto[]>([]);
const recentlyIgnoredApp = ref<IgnoredNetworkAppDto | null>(null);
const isIgnoredAppsExpanded = ref(true);
const ignoredAppsPanel = ref<HTMLElement | null>(null);
const selectedApp = ref<AppTrafficSummaryDto | null>(null);
const appSegments = ref<AppTrafficSegmentDto[]>([]);
const selectedSegmentIndex = ref<number | null>(null);
const overviewSummary = ref<NetworkPeriodSummaryDto | null>(null);
const totalsSummary = ref<NetworkPeriodSummaryDto | null>(null);
const isLoading = ref(false);
const isSegmentsLoading = ref(false);
const isIgnoreMutationPending = ref(false);
const loadingSource = ref<'filter' | 'manual'>('filter');
const errorMessage = ref('');
const settingsDefaultWarning = ref('');
const segmentsErrorMessage = ref('');
const isMobileViewport = ref(false);
const isMobileFiltersExpanded = ref(false);
const appliedFilters = ref<NetworkFilters | null>(null);
const dialogTrigger = ref<HTMLElement | null>(null);
let autoRefreshTimer: number | null = null;
let mobileViewportQuery: MediaQueryList | null = null;
let dashboardAbortController: AbortController | null = null;
let pendingReloadSource: 'filter' | 'manual' = 'filter';
let ignoreUndoTimer: number | null = null;
// 应用明细请求可能被切换筛选条件、关闭弹层或重新选择应用打断；版本号用于丢弃过期响应。
let segmentsRequestVersion = 0;
let dashboardRequestVersion = 0;
let isViewActive = false;

interface NetworkFilters {
  from: string;
  to: string;
  topN: number;
  scope: 'all' | 'wan' | 'lan' | 'loopback';
  direction: 'total' | 'upload' | 'download';
}

// 查询条件：分别驱动筛选区、占比面板和应用排行
const filters = reactive({
  from: toLocalInputValue(new Date(Date.now() - 24 * 60 * 60 * 1000)),
  to: toLocalInputValue(new Date()),
  topN: 10,
  scope: 'wan' as 'all' | 'wan' | 'lan' | 'loopback',
  direction: 'upload' as 'total' | 'upload' | 'download'
});
const selectedPresetHours = ref<number | null>(24);
let presetSignature = '';
let applyingDefaultTopN = false;
let topNUserTouched = false;

// 累计上传/下载 - 使用 totalsSummary（保持当前 scope，但不受 direction 筛选影响）
const totalUploadBytes = computed(() =>
  totalsSummary.value?.totalUploadBytes ?? 0
);

const totalDownloadBytes = computed(() =>
  totalsSummary.value?.totalDownloadBytes ?? 0
);

const uploadDownloadTotalBytes = computed(() =>
  totalUploadBytes.value + totalDownloadBytes.value
);

const uploadPercent = computed(() =>
  uploadDownloadTotalBytes.value === 0 ? 0 : (totalUploadBytes.value / uploadDownloadTotalBytes.value) * 100
);

const downloadPercent = computed(() =>
  uploadDownloadTotalBytes.value === 0 ? 0 : (totalDownloadBytes.value / uploadDownloadTotalBytes.value) * 100
);

const dominantTrafficDirectionLabel = computed(() => {
  if (uploadDownloadTotalBytes.value === 0) return '--';
  if (Math.abs(uploadPercent.value - downloadPercent.value) < 5) return '基本均衡';
  return uploadPercent.value >= downloadPercent.value ? '上传为主' : '下载为主';
});

const dominantTrafficDirectionHint = computed(() => {
  if (uploadDownloadTotalBytes.value === 0) return '当前没有流量数据';
  return `差值 ${Math.abs(uploadPercent.value - downloadPercent.value).toFixed(1)}%`;
});

// 占比面板数据 - 使用 overviewSummary（不受 scope 筛选影响，但受 direction 和时间范围影响）
const overviewWanTotalBytes = computed(() =>
  (overviewSummary.value?.wanUploadBytes ?? 0) + (overviewSummary.value?.wanDownloadBytes ?? 0)
);

const overviewLanTotalBytes = computed(() =>
  (overviewSummary.value?.lanUploadBytes ?? 0) + (overviewSummary.value?.lanDownloadBytes ?? 0)
);

const overviewLoopbackTotalBytes = computed(() =>
  (overviewSummary.value?.loopbackUploadBytes ?? 0) + (overviewSummary.value?.loopbackDownloadBytes ?? 0)
);

const overviewOtherTotalBytes = computed(() =>
  (overviewSummary.value?.otherUploadBytes ?? 0) + (overviewSummary.value?.otherDownloadBytes ?? 0)
);

const overviewTotalBytes = computed(() =>
  overviewWanTotalBytes.value + overviewLanTotalBytes.value + overviewLoopbackTotalBytes.value + overviewOtherTotalBytes.value
);

const wanPercent = computed(() =>
  overviewTotalBytes.value === 0 ? 0 : (overviewWanTotalBytes.value / overviewTotalBytes.value) * 100
);

const lanPercent = computed(() =>
  overviewTotalBytes.value === 0 ? 0 : (overviewLanTotalBytes.value / overviewTotalBytes.value) * 100
);

const loopbackPercent = computed(() =>
  overviewTotalBytes.value === 0 ? 0 : (overviewLoopbackTotalBytes.value / overviewTotalBytes.value) * 100
);

const otherPercent = computed(() =>
  overviewTotalBytes.value === 0 ? 0 : (overviewOtherTotalBytes.value / overviewTotalBytes.value) * 100
);

// 兼容旧代码，使用 overview 数据
const wanTotalBytes = overviewWanTotalBytes;
const lanTotalBytes = overviewLanTotalBytes;
const loopbackTotalBytes = overviewLoopbackTotalBytes;
const otherTotalBytes = overviewOtherTotalBytes;

const ignoredAppKeys = computed(() => new Set(ignoredApps.value.map((item) => item.appKey)));
const topRanking = computed(() => {
  if (!appliedFilters.value) {
    return items.value
      .filter((item) => !ignoredAppKeys.value.has(item.appKey))
      .slice(0, filters.topN);
  }

  return items.value
    .filter((item) => !ignoredAppKeys.value.has(item.appKey))
    .slice(0, appliedFilters.value.topN);
});
const ignoredAppRows = computed(() =>
  ignoredApps.value.map((record) => ({
    record,
    summary: items.value.find((item) => item.appKey === record.appKey) ?? null
  }))
);
const rankingSkeletonRows = [1, 2, 3, 4] as const;
const showRankingSkeleton = computed(() => isLoading.value && (!topRanking.value.length || loadingSource.value === 'filter'));
const rankingMaxValue = computed(() =>
  topRanking.value.reduce((max, item) => Math.max(max, getRankingValue(item)), 0)
);
const segmentMaxValue = computed(() =>
  appSegments.value.reduce((max, segment) => Math.max(max, getSegmentValue(segment)), 0)
);
const selectedSegment = computed(() =>
  selectedSegmentIndex.value == null ? null : appSegments.value[selectedSegmentIndex.value] ?? null
);

const rankingDescription = computed(() => {
  const activeFilters = appliedFilters.value ?? filters;
  const scopeLabel = activeFilters.scope === 'wan' ? 'WAN' : activeFilters.scope === 'lan' ? 'LAN' : activeFilters.scope === 'loopback' ? 'Loopback' : '全部';
  const directionLabel =
    activeFilters.direction === 'upload' ? '上传' : activeFilters.direction === 'download' ? '下载' : '总流量';
  return `${scopeLabel} / ${directionLabel}`;
});

const hasPendingFilterChanges = computed(() => {
  if (!appliedFilters.value) return false;
  const snapshot = createFilterSnapshot();
  if ('error' in snapshot) return true;
  return JSON.stringify(snapshot) !== JSON.stringify(appliedFilters.value);
});

const segmentDurationLabel = computed(() => {
  const activeFilters = appliedFilters.value ?? filters;
  const from = activeFilters.from ? new Date(activeFilters.from) : null;
  const to = activeFilters.to ? new Date(activeFilters.to) : null;
  if (!from || !to || Number.isNaN(from.getTime()) || Number.isNaN(to.getTime())) {
    return '--';
  }

  const durationHours = (to.getTime() - from.getTime()) / (60 * 60 * 1000);
  if (durationHours <= 1.5) return '约 1 小时';
  if (durationHours <= 12.5) return '约 12 小时';
  if (durationHours <= 36) return '约 1 天';
  return `约 ${Math.round(durationHours / 24)} 天`;
});

const selectedAppOutsideRanking = computed(() =>
  selectedApp.value !== null && !topRanking.value.some((item) => item.appKey === selectedApp.value?.appKey)
);

const activePresetHours = computed(() => selectedPresetHours.value);
const showAdvancedFilters = computed(() => !isMobileViewport.value || isMobileFiltersExpanded.value);

const rangeParts = computed(() => formatRangeParts());

const dominantScopeLabel = computed(() => {
  if (overviewTotalBytes.value === 0) return '--';
  const entries = [
    ['WAN', wanPercent.value],
    ['LAN', lanPercent.value],
    ['Loopback', loopbackPercent.value],
    ['其他', otherPercent.value]
  ] as const;
  const [label, percent] = entries.reduce((winner, entry) => entry[1] > winner[1] ? entry : winner);
  return percent > 50 ? `${label} 为主` : '分布较均衡';
});

const dominantScopeHint = computed(() => {
  if (overviewTotalBytes.value === 0) return '当前没有流量数据';
  return `最高占比 ${Math.max(wanPercent.value, lanPercent.value, loopbackPercent.value, otherPercent.value).toFixed(1)}%`;
});

onMounted(() => {
  isViewActive = true;
  if (typeof window !== 'undefined' && 'matchMedia' in window) {
    mobileViewportQuery = window.matchMedia('(max-width: 720px)');
    syncMobileViewportState(mobileViewportQuery.matches);
    mobileViewportQuery.addEventListener('change', handleMobileViewportChange);
  }

  void initializeNetworkPage();
});

onUnmounted(() => {
  isViewActive = false;
  segmentsRequestVersion++;
  dashboardAbortController?.abort();

  if (autoRefreshTimer !== null) {
    window.clearTimeout(autoRefreshTimer);
    autoRefreshTimer = null;
  }

  if (ignoreUndoTimer !== null) {
    window.clearTimeout(ignoreUndoTimer);
    ignoreUndoTimer = null;
  }

  if (mobileViewportQuery) {
    mobileViewportQuery.removeEventListener('change', handleMobileViewportChange);
    mobileViewportQuery = null;
  }

});

watch(
  () => [filters.from, filters.to, filters.topN, filters.scope, filters.direction],
  () => {
    if (presetSignature === `${filters.from}|${filters.to}`) {
      presetSignature = '';
    } else {
      selectedPresetHours.value = null;
    }

    if (!applyingDefaultTopN) {
      topNUserTouched = true;
    }

    // 多个筛选控件可能连续变化，统一 debounce 后再请求，减少无效接口调用和骨架屏闪烁。
    if (autoRefreshTimer !== null) {
      window.clearTimeout(autoRefreshTimer);
    }

    autoRefreshTimer = window.setTimeout(() => {
      autoRefreshTimer = null;
      const source = pendingReloadSource;
      pendingReloadSource = 'filter';
      void loadApps(source);
    }, 250);
  }
);

async function loadApps(source: 'filter' | 'manual' = 'filter') {
  dashboardAbortController?.abort();
  const requestController = new AbortController();
  const requestVersion = ++dashboardRequestVersion;
  dashboardAbortController = requestController;
  loadingSource.value = source;
  isLoading.value = true;
  errorMessage.value = '';

  const snapshot = createFilterSnapshot();
  if ('error' in snapshot) {
    errorMessage.value = snapshot.error;
    isLoading.value = false;
    dashboardAbortController = null;
    return;
  }

  try {
    const dashboard = await getNetworkDashboard({
      from: snapshot.from,
      to: snapshot.to,
      topN: filters.topN,
      scope: filters.scope,
      direction: filters.direction,
      signal: requestController.signal
    });

    if (!isViewActive || requestVersion !== dashboardRequestVersion) {
      return;
    }

    items.value = dashboard.apps;
    ignoredApps.value = dashboard.ignoredApps ?? [];
    overviewSummary.value = dashboard.overview;
    totalsSummary.value = dashboard.totals;
    appliedFilters.value = snapshot;
    syncSelectedAppAfterRankingLoad(dashboard.apps);
    if (selectedApp.value) {
      void loadSelectedAppSegments();
    }
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      return;
    }
    if (!isViewActive || requestVersion !== dashboardRequestVersion) {
      return;
    }
    const detail = error instanceof Error ? error.message : '加载网络汇总失败。';
    errorMessage.value = appliedFilters.value
      ? `本次加载失败，仍显示上次成功结果：${detail}`
      : detail;
  } finally {
    if (dashboardAbortController === requestController) {
      dashboardAbortController = null;
      isLoading.value = false;
    }
  }
}

function refreshApps() {
  if (autoRefreshTimer !== null) {
    window.clearTimeout(autoRefreshTimer);
    autoRefreshTimer = null;
  }

  // 当前时间范围命中预设时，手动刷新会先重算“到当前时间”的范围，再由 watcher 触发加载。
  const presetHours = activePresetHours.value;
  if (presetHours) {
    pendingReloadSource = 'manual';
    applyPreset(presetHours);
    return;
  }

  void loadApps('manual');
}

function selectApp(item: AppTrafficSummaryDto) {
  if (!selectedApp.value) {
    dialogTrigger.value = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  }
  selectedApp.value = item;
  void loadSelectedAppSegments();
}

async function ignoreApp(item: AppTrafficSummaryDto) {
  if (ignoredAppKeys.value.has(item.appKey) || isIgnoreMutationPending.value) {
    return;
  }

  const record: IgnoredNetworkAppDto = {
    appKey: item.appKey,
    processName: item.processName,
    displayName: item.displayName ?? null,
    executablePath: item.executablePath ?? null
  };

  isIgnoreMutationPending.value = true;
  errorMessage.value = '';
  try {
    ignoredApps.value = await ignoreNetworkApp(record);
    isIgnoredAppsExpanded.value = true;
    recentlyIgnoredApp.value = record;
    scheduleIgnoreUndoDismiss();

    if (selectedApp.value?.appKey === item.appKey) {
      closeAppSegmentsPanel();
    }

    await loadApps('manual');
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '保存全局忽略设置失败。';
  } finally {
    isIgnoreMutationPending.value = false;
  }
}

async function restoreIgnoredApp(appKey: string) {
  if (isIgnoreMutationPending.value) {
    return;
  }

  isIgnoreMutationPending.value = true;
  errorMessage.value = '';
  try {
    ignoredApps.value = await restoreNetworkApp(appKey);

    if (recentlyIgnoredApp.value?.appKey === appKey) {
      clearIgnoreUndo();
    }

    await loadApps('manual');
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '恢复全局排行设置失败。';
  } finally {
    isIgnoreMutationPending.value = false;
  }
}

function undoLastIgnore() {
  const app = recentlyIgnoredApp.value;
  if (!app) {
    return;
  }

  void restoreIgnoredApp(app.appKey);
}

function scrollToIgnoredApps() {
  if (!ignoredApps.value.length) {
    return;
  }

  isIgnoredAppsExpanded.value = true;
  void nextTick(() => {
    ignoredAppsPanel.value?.scrollIntoView({
      behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth',
      block: 'nearest'
    });
  });
}

async function initializeNetworkPage() {
  let migrationError = '';
  try {
    await migrateLegacyIgnoredApps();
  } catch (error) {
    migrationError = error instanceof Error ? error.message : '迁移旧版忽略列表失败。';
  }

  try {
    const settings = await getSettings();
    if (isViewActive && !topNUserTouched) {
      applyingDefaultTopN = true;
      filters.topN = settings.topNDefault;
      applyingDefaultTopN = false;
      settingsDefaultWarning.value = '';
    }
  } catch {
    if (isViewActive) {
      settingsDefaultWarning.value = '未读取到默认排行数量，已使用 10 条；可在设置页调整。';
    }
  }

  if (isViewActive) {
    await loadApps('filter');
  }
  if (migrationError && !errorMessage.value) {
    errorMessage.value = `${migrationError}；旧数据已保留，可刷新后重试。`;
  }
}

async function migrateLegacyIgnoredApps() {
  const legacyApps = loadLegacyIgnoredApps();
  if (!legacyApps.length) {
    return;
  }

  for (const app of legacyApps) {
    ignoredApps.value = await ignoreNetworkApp(app);
  }

  window.localStorage.removeItem(ignoredAppsStorageKey);
}

function loadLegacyIgnoredApps(): IgnoredNetworkAppDto[] {
  try {
    const rawValue = window.localStorage.getItem(ignoredAppsStorageKey);
    if (!rawValue) {
      return [];
    }

    const parsedValue: unknown = JSON.parse(rawValue);
    if (!Array.isArray(parsedValue)) {
      return [];
    }

    const seenKeys = new Set<string>();
    return parsedValue.flatMap((value) => {
      if (!isIgnoredAppRecord(value) || seenKeys.has(value.appKey)) {
        return [];
      }

      seenKeys.add(value.appKey);
      return [value];
    });
  } catch {
    return [];
  }
}

function isIgnoredAppRecord(value: unknown): value is IgnoredNetworkAppDto {
  if (!value || typeof value !== 'object') {
    return false;
  }

  const record = value as Record<string, unknown>;
  return (
    typeof record.appKey === 'string'
    && typeof record.processName === 'string'
    && (typeof record.displayName === 'string' || record.displayName === null)
    && (typeof record.executablePath === 'string' || record.executablePath === null)
  );
}

function scheduleIgnoreUndoDismiss() {
  if (ignoreUndoTimer !== null) {
    window.clearTimeout(ignoreUndoTimer);
  }

  ignoreUndoTimer = window.setTimeout(() => {
    ignoreUndoTimer = null;
    recentlyIgnoredApp.value = null;
  }, 6000);
}

function clearIgnoreUndo() {
  recentlyIgnoredApp.value = null;
  if (ignoreUndoTimer !== null) {
    window.clearTimeout(ignoreUndoTimer);
    ignoreUndoTimer = null;
  }
}

function closeAppSegmentsPanel() {
  selectedApp.value = null;
  appSegments.value = [];
  selectedSegmentIndex.value = null;
  segmentsErrorMessage.value = '';
  isSegmentsLoading.value = false;
  segmentsRequestVersion++;
  restoreDialogFocus();
}

async function loadSelectedAppSegments() {
  const app = selectedApp.value;
  if (!app) {
    appSegments.value = [];
    segmentsErrorMessage.value = '';
    return;
  }

  // 记录本次请求版本；响应回来时只有仍是最新版本，才允许写入弹层状态。
  const requestVersion = ++segmentsRequestVersion;
  isSegmentsLoading.value = true;
  segmentsErrorMessage.value = '';
  selectedSegmentIndex.value = null;

  try {
    const segments = await getNetworkAppSegments(app.appKey, {
      from: toIsoString((appliedFilters.value ?? filters).from),
      to: toIsoString((appliedFilters.value ?? filters).to),
      scope: (appliedFilters.value ?? filters).scope,
      direction: (appliedFilters.value ?? filters).direction
    });

    if (requestVersion !== segmentsRequestVersion) {
      return;
    }

    appSegments.value = segments;
    selectedSegmentIndex.value = null;
  } catch (error) {
    if (requestVersion !== segmentsRequestVersion) {
      return;
    }

    segmentsErrorMessage.value = error instanceof Error ? error.message : '加载应用分段流量失败。';
    appSegments.value = [];
  } finally {
    if (requestVersion === segmentsRequestVersion) {
      isSegmentsLoading.value = false;
    }
  }
}

function syncSelectedAppAfterRankingLoad(apps: AppTrafficSummaryDto[]) {
  // 排行刷新后如果选中应用仍在结果中，用新汇总数据替换旧对象，保持弹层标题和数值同步。
  if (!selectedApp.value) {
    return;
  }

  const refreshedApp = apps.find((item) => item.appKey === selectedApp.value?.appKey);
  if (refreshedApp) {
    selectedApp.value = refreshedApp;
  }
}

function toggleMobileFilters() {
  if (!isMobileViewport.value) {
    return;
  }

  isMobileFiltersExpanded.value = !isMobileFiltersExpanded.value;
}

function handleMobileViewportChange(event: MediaQueryListEvent) {
  syncMobileViewportState(event.matches);
}

function syncMobileViewportState(matches: boolean) {
  isMobileViewport.value = matches;
}

function applyPreset(hours: number) {
  const now = new Date();
  const to = toLocalInputValue(now);
  const from = toLocalInputValue(new Date(now.getTime() - hours * 60 * 60 * 1000));
  presetSignature = `${from}|${to}`;
  selectedPresetHours.value = hours;
  filters.to = to;
  filters.from = from;
}

function focusNetworkFilters() {
  const firstFilter = document.querySelector<HTMLElement>('#network-filters-toolbar input, #network-filters-toolbar select');
  if (isMobileViewport.value && !isMobileFiltersExpanded.value) {
    isMobileFiltersExpanded.value = true;
  }
  void nextTick(() => firstFilter?.focus());
}

function formatRangeLabel() {
  const parts = rangeParts.value;
  return parts ? `${parts.from} ~ ${parts.to}` : '时间范围无效';
}

function formatBytes(value: number) {
  if (!Number.isFinite(value) || value < 0) return '--';
  if (value < 1024) return `${value} B`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`;
  if (value < 1024 * 1024 * 1024) return `${(value / 1024 / 1024).toFixed(2)} MB`;
  return `${(value / 1024 / 1024 / 1024).toFixed(2)} GB`;
}

function formatRatioPercent(total: number, percent: number) {
  return total > 0 && Number.isFinite(percent) ? `${percent.toFixed(1)}%` : '--';
}

function formatRangeParts() {
  const activeFilters = appliedFilters.value ?? filters;
  const from = activeFilters.from ? new Date(activeFilters.from) : null;
  const to = activeFilters.to ? new Date(activeFilters.to) : null;

  if (!from || !to || Number.isNaN(from.getTime()) || Number.isNaN(to.getTime())) {
    return null;
  }

  return {
    from: from.toLocaleString(),
    to: to.toLocaleString()
  };
}

function toLocalInputValue(value: Date) {
  const timezoneOffset = value.getTimezoneOffset() * 60_000;
  return new Date(value.getTime() - timezoneOffset).toISOString().slice(0, 16);
}

function toIsoString(value: string) {
  return value ? new Date(value).toISOString() : undefined;
}

function createFilterSnapshot(): NetworkFilters | { error: string } {
  const fromDate = filters.from ? new Date(filters.from) : null;
  const toDate = filters.to ? new Date(filters.to) : null;

  if (!fromDate || Number.isNaN(fromDate.getTime()) || !toDate || Number.isNaN(toDate.getTime())) {
    return { error: '请选择有效的开始时间和结束时间。' };
  }

  if (toDate.getTime() <= fromDate.getTime()) {
    return { error: '结束时间必须晚于开始时间。' };
  }

  if (!Number.isInteger(filters.topN) || filters.topN < 1 || filters.topN > 100) {
    return { error: '排行数量必须是 1 ~ 100 之间的整数。' };
  }

  return {
    from: fromDate.toISOString(),
    to: toDate.toISOString(),
    topN: filters.topN,
    scope: filters.scope,
    direction: filters.direction
  };
}

function getMatchedPresetHours(fromValue: string, toValue: string) {
  const from = fromValue ? new Date(fromValue) : null;
  const to = toValue ? new Date(toValue) : null;

  if (!from || !to || Number.isNaN(from.getTime()) || Number.isNaN(to.getTime())) {
    return null;
  }

  const diffHours = (to.getTime() - from.getTime()) / (60 * 60 * 1000);
  const rounded = Math.round(diffHours * 100) / 100;
  return presetOptions.find((preset) => rounded === preset.hours)?.hours ?? null;
}

function getRankingValue(item: AppTrafficSummaryDto) {
  const activeFilters = appliedFilters.value ?? filters;
  if (activeFilters.scope === 'wan') {
    if (activeFilters.direction === 'upload') return item.wanUploadBytes;
    if (activeFilters.direction === 'download') return item.wanDownloadBytes;
    return item.wanUploadBytes + item.wanDownloadBytes;
  }

  if (activeFilters.scope === 'lan') {
    if (activeFilters.direction === 'upload') return item.lanUploadBytes;
    if (activeFilters.direction === 'download') return item.lanDownloadBytes;
    return item.lanUploadBytes + item.lanDownloadBytes;
  }

  if (activeFilters.scope === 'loopback') {
    if (activeFilters.direction === 'upload') return item.loopbackUploadBytes;
    if (activeFilters.direction === 'download') return item.loopbackDownloadBytes;
    return item.loopbackUploadBytes + item.loopbackDownloadBytes;
  }

  if (activeFilters.direction === 'upload') return item.totalUploadBytes;
  if (activeFilters.direction === 'download') return item.totalDownloadBytes;
  return item.totalUploadBytes + item.totalDownloadBytes;
}

function getScopedUploadBytes(item: AppTrafficSummaryDto) {
  const activeFilters = appliedFilters.value ?? filters;
  if (activeFilters.scope === 'wan') return item.wanUploadBytes;
  if (activeFilters.scope === 'lan') return item.lanUploadBytes;
  if (activeFilters.scope === 'loopback') return item.loopbackUploadBytes;
  return item.totalUploadBytes;
}

function getScopedDownloadBytes(item: AppTrafficSummaryDto) {
  const activeFilters = appliedFilters.value ?? filters;
  if (activeFilters.scope === 'wan') return item.wanDownloadBytes;
  if (activeFilters.scope === 'lan') return item.lanDownloadBytes;
  if (activeFilters.scope === 'loopback') return item.loopbackDownloadBytes;
  return item.totalDownloadBytes;
}

function getRankingPercent(item: AppTrafficSummaryDto) {
  const value = getRankingValue(item);
  if (rankingMaxValue.value <= 0) {
    return 0;
  }

  if (value <= 0) {
    return 0;
  }

  return Math.min(100, (value / rankingMaxValue.value) * 100);
}

function getSegmentValue(segment: AppTrafficSegmentDto) {
  const activeFilters = appliedFilters.value ?? filters;
  if (activeFilters.scope === 'wan') {
    if (activeFilters.direction === 'upload') return segment.wanUploadBytes;
    if (activeFilters.direction === 'download') return segment.wanDownloadBytes;
    return segment.wanUploadBytes + segment.wanDownloadBytes;
  }

  if (activeFilters.scope === 'lan') {
    if (activeFilters.direction === 'upload') return segment.lanUploadBytes;
    if (activeFilters.direction === 'download') return segment.lanDownloadBytes;
    return segment.lanUploadBytes + segment.lanDownloadBytes;
  }

  if (activeFilters.scope === 'loopback') {
    if (activeFilters.direction === 'upload') return segment.loopbackUploadBytes;
    if (activeFilters.direction === 'download') return segment.loopbackDownloadBytes;
    return segment.loopbackUploadBytes + segment.loopbackDownloadBytes;
  }

  if (activeFilters.direction === 'upload') return segment.totalUploadBytes;
  if (activeFilters.direction === 'download') return segment.totalDownloadBytes;
  return segment.totalUploadBytes + segment.totalDownloadBytes;
}

function getSegmentUploadBytes(segment: AppTrafficSegmentDto) {
  const activeFilters = appliedFilters.value ?? filters;
  if (activeFilters.scope === 'wan') return segment.wanUploadBytes;
  if (activeFilters.scope === 'lan') return segment.lanUploadBytes;
  if (activeFilters.scope === 'loopback') return segment.loopbackUploadBytes;
  return segment.totalUploadBytes;
}

function getSegmentDownloadBytes(segment: AppTrafficSegmentDto) {
  const activeFilters = appliedFilters.value ?? filters;
  if (activeFilters.scope === 'wan') return segment.wanDownloadBytes;
  if (activeFilters.scope === 'lan') return segment.lanDownloadBytes;
  if (activeFilters.scope === 'loopback') return segment.loopbackDownloadBytes;
  return segment.totalDownloadBytes;
}

function getSegmentPercent(segment: AppTrafficSegmentDto) {
  const value = getSegmentValue(segment);
  if (segmentMaxValue.value <= 0 || value <= 0) {
    return 0;
  }

  return Math.min(100, (value / segmentMaxValue.value) * 100);
}

function selectSegment(index: number) {
  if (index < 0 || index >= appSegments.value.length) return;
  selectedSegmentIndex.value = index;
}

function handleSegmentKeydown(event: KeyboardEvent, index: number) {
  if (event.key !== 'Enter' && event.key !== ' ') return;
  event.preventDefault();
  selectSegment(index);
}

function formatSegmentRange(segment: AppTrafficSegmentDto) {
  const from = new Date(segment.from);
  const to = new Date(segment.to);
  if (Number.isNaN(from.getTime()) || Number.isNaN(to.getTime())) {
    return '--';
  }

  return `${from.toLocaleString()} ~ ${to.toLocaleString()}`;
}

function formatSegmentAxisLabel(segment: AppTrafficSegmentDto) {
  const from = new Date(segment.from);
  if (Number.isNaN(from.getTime())) {
    return '--';
  }

  const month = String(from.getMonth() + 1).padStart(2, '0');
  const day = String(from.getDate()).padStart(2, '0');
  const hour = String(from.getHours()).padStart(2, '0');
  return segmentDurationLabel.value === '1 小时' ? `${hour}:00` : `${month}-${day}`;
}

function getSegmentTooltip(segment: AppTrafficSegmentDto) {
  return [
    formatSegmentRange(segment),
    `流量 ${formatBytes(getSegmentValue(segment))}`,
    `上传 ${formatBytes(getSegmentUploadBytes(segment))}`,
    `下载 ${formatBytes(getSegmentDownloadBytes(segment))}`
  ].join('\n');
}

function getSegmentTooltipPlacementClass(index: number) {
  if (index < 2) {
    return 'app-segment-bar-item-left-edge';
  }

  if (index >= appSegments.value.length - 2) {
    return 'app-segment-bar-item-right-edge';
  }

  return '';
}
</script>
