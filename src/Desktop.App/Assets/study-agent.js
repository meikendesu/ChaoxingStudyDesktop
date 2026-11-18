(() => {
  'use strict';
  const allowed = () => location.protocol === 'https:' &&
    (location.hostname === 'study-desktop.local' || /(^|\.)(chaoxing|xuexitong)\.com$/.test(location.hostname));
  if (!allowed() || window.__studyDesktop) return;
  const documentId = crypto.randomUUID();
  let playbackProtected = false;
  const setPlaybackProtection = value => { playbackProtected = value === true; return playbackProtected; };
  // 文档创建时先于页面脚本注册；只在用户启动自动播放后阻止离开事件触发的暂停。
  const guardPlayback = event => {
    if (!playbackProtected) return;
    if (event.type === 'blur' && event.target !== window) return;
    if ((event.type === 'mouseout' || event.type === 'mouseleave') && event.relatedTarget) return;
    event.stopImmediatePropagation();
    if (event.cancelable) event.preventDefault();
  };
  for (const type of ['mouseout', 'mouseleave', 'blur', 'visibilitychange'])
    window.addEventListener(type, guardPlayback, true);
  let characterMap = {};
  let fallbackCodes = new Set();
  let fontAbort = null;
  let fontState = { status: 'idle' };
  let fontStage = 'idle';
  let fontSourceCount = 0;
  const locallyVisible = el => {
    for (let node = el; node?.nodeType === 1; node = node.parentElement) {
      const css = node.ownerDocument.defaultView.getComputedStyle(node);
      // aria-hidden 只影响无障碍树，不决定屏幕上是否可见。
      if (node.hidden || css.display === 'none' || css.visibility === 'hidden' || css.visibility === 'collapse') return false;
    }
    return true;
  };
  const visible = el => {
    if (!locallyVisible(el)) return false;
    // iframe 内的元素自身可见，不代表承载它的任务点可见。
    // 按元素所属文档检查同源祖先，避免使用当前 realm 的 instanceof 漏过父页节点。
    try {
      let owner = el.ownerDocument.defaultView;
      while (owner && owner !== owner.parent) {
        const frame = owner.frameElement;
        if (!frame) break;
        if (!locallyVisible(frame)) return false;
        owner = frame.ownerDocument.defaultView;
      }
    } catch (_) { /* 跨域祖先不可直接检查，不据未知状态隐藏文档。 */ }
    return true;
  };
  const mediaEvents = [];
  const observedMedia = new WeakSet();
  const recordMedia = (video, kind) => {
    mediaEvents.push({ kind, at: Date.now(), paused: video.paused, ended: video.ended,
      position: Number.isFinite(video.currentTime) ? video.currentTime : null,
      duration: Number.isFinite(video.duration) ? video.duration : null,
      muted: video.muted, rate: video.playbackRate, ready: video.readyState,
      visibility: document.visibilityState, focus: document.hasFocus(), error: video.error?.code || 0 });
    if (mediaEvents.length > 24) mediaEvents.shift();
  };
  const audioPlayerContainer = media => media.matches('audio.vjs-tech') ? media.closest('.video-js.vjs-audio') : null;
  const mediaVisible = media => {
    if (visible(media)) return true;
    // 超星音频由可见的 Video.js 控件承载，实际 audio tech 固定 display:none。
    // 只放行该播放器的媒体，不能把后台试听音频或隐藏任务一起放行。
    const container = audioPlayerContainer(media);
    const css = getComputedStyle(media);
    return !!container && !media.hidden && css.display === 'none' && css.visibility === 'visible' && visible(container);
  };
  const mediaElements = () => [...document.querySelectorAll('video,audio')].filter(mediaVisible).map(video => {
    if (!observedMedia.has(video)) {
      observedMedia.add(video);
      for (const event of ['playing','pause','ended','waiting','stalled','error','ratechange','volumechange'])
        video.addEventListener(event, () => recordMedia(video, event));
      recordMedia(video, 'observed');
    }
    return video;
  });
  const mediaLabel = list => list.some(v => v.tagName === 'AUDIO') ? list.some(v => v.tagName === 'VIDEO') ? '音视频' : '音频' : '视频';
  const pauseMedia = (video, reason) => { recordMedia(video, reason); video.pause(); };
  const questionSelector = '.questionLi,.singleQuesId,.multipleQuesId,.judgementQuesId,.completionQuesId,.examItem,.questionItem,.TiMu,.examQ,.examQuestion,.subject-item,.exam-paper-question,.question-item,.quesItem,.quesBody,.mark_table';
  const questionNodes = () => {
    // 批阅结果沿用答题容器和选项样式，但已不可作答；不再请求模型或尝试填写。
    if (/\/work\/selectWorkQuestionYiPiYue(?:\/|$)/i.test(location.pathname) ||
        /查看已批阅作业|查看已提交作业/.test(document.title) && document.querySelector('.newAnswerBx,.myAnswerBx')) return [];
    const nodes = [...document.querySelectorAll(questionSelector)].filter(el => visible(el) && !el.closest('.exam-sidebar,.leftItems,.examPaperNum-list,.examNum-list,.test-num-list,.num-list'));
    const children = new Map(nodes.map(node => [node, []]));
    const roots = [];
    for (const node of nodes) {
      const parent = node.parentElement?.closest(questionSelector);
      if (children.has(parent)) children.get(parent).push(node); else roots.push(node);
    }
    const leafCount = node => children.get(node).length ? children.get(node).reduce((sum, child) => sum + leafCount(child), 0) : 1;
    const atomic = node => leafCount(node) > 1 ? children.get(node).flatMap(atomic) : [node];
    return roots.flatMap(atomic);
  };
  const anonymousQuestionIds = new WeakMap();
  let nextQuestionId = 0;
  const questionPageSelector = '.exam-sidebar,.leftItems,.questionList,.TiMu-list,.examPaperNum-list,.examNum-list,.markBox,.test-num-list,.exam-paper-num,.questionStatus,.nav-list,.num-list';
  const activeQuestionPage = () => [...document.querySelectorAll(questionPageSelector)].flatMap(root => [...root.querySelectorAll('.active,.current,.cur,.on,.selected,.currents,[aria-current=page],[aria-current=true],[aria-selected=true]')])
    .find(el => visible(el) && /^\d{1,4}$/.test((el.innerText || el.textContent || '').trim()));
  const siteQuestionKey = node => node.getAttribute('data-question-id') || node.getAttribute('data-id') ||
    [...node.querySelectorAll('input[name]')].map(input => input.name.match(/^(?:answer|answertype|questionId|quesId)[_-]?(\d+)$/)?.[1]).find(Boolean) || node.id;
  const questionKey = (node, index) => {
    const page = activeQuestionPage();
    const pageKey = page ? '@page:' + (page.getAttribute('data-question-id') || page.id || text(page)) : '';
    const explicit = siteQuestionKey(node);
    if (explicit) return 'site:' + explicit + pageKey;
    if (pageKey) return pageKey + ':slot:' + index;
    if (!anonymousQuestionIds.has(node)) anonymousQuestionIds.set(node, 'node:' + (++nextQuestionId));
    return anonymousQuestionIds.get(node);
  };
  const inputs = node => [...node.querySelectorAll('input[type=radio],input[type=checkbox]')];
  const optionPairs = node => {
    const pairs = new Map();
    const add = (source, input = null) => {
      // 先定位完整选项行；行内 label.fl 只是 A/B 字母标签，不能独立成为选项。
      const row = source.closest('[role=radio],[role=checkbox],li,.Cy_ulTop .clearfix,.option_li,.answer_p,.textDIV,.eidtDiv,div.option') ||
        source.closest('label.option,label.fl,.answerBg label') ||
        (input?.id && node.ownerDocument.querySelector(`label[for="${CSS.escape(input.id)}"]`)) || source.closest('label') || source.parentElement;
      if (!row || row === node || !visible(row) || row.closest('.blankItemDiv,.edui-editor,[contenteditable=true]') ||
          row.querySelector('input[type=text],textarea,.edui-editor') || inputs(row).length > 1) return;
      const textElement = [...row.querySelectorAll('.after')].find(visible) || row;
      if (!decodedText(textElement).trim()) return;
      const existing = pairs.get(row);
      const actualInput = input || existing?.input || inputs(row)[0] || null;
      // 原脚本 _extractOptionPairs.pickText 优先读取选项文本节点自身的 aria-label，
      // 再退到 input；此前只检查了选项行容器，会漏掉 .after 上的可访问文本。
      const accessible = (node.matches('.font-cxsecret') || node.querySelector('.font-cxsecret'))
        ? (textElement?.getAttribute('aria-label') || row.getAttribute('aria-label') ||
            actualInput?.getAttribute('aria-label') || '').trim() : '';
      pairs.set(row, { element: row, input: actualInput, textElement, accessible });
    };
    inputs(node).forEach(input => add(input, input));
    node.querySelectorAll('[role=radio],[role=checkbox],.stem_answer .answerBg .answer_p,.Cy_ulTop .clearfix,.Zy_ulTop li,ul li .after,.topic-option-list li,.answerBg li,.option_li,.answer_p,.textDIV,.eidtDiv,label.fl,label.option,.answerBg label,.Zy_ulBottom li,.answerList li,div.option')
      .forEach(element => add(element));
    return [...pairs.values()].sort((a, b) => a.element.compareDocumentPosition(b.element) & Node.DOCUMENT_POSITION_FOLLOWING ? -1 : 1);
  };
  const selected = pair => pair.input ? pair.input.checked : pair.element.getAttribute('aria-checked') === 'true' ||
    pair.element.matches('.is-checked,.checked,.selected,.active,.cur') || !!pair.element.querySelector('.is-checked,[aria-checked=true],input:checked');
  const blanks = node => [...node.querySelectorAll('input[type=text],input:not([type]),textarea,.edui-editor-iframeholder iframe,.blankItemDiv .edui-editor iframe')]
    .filter(element => visible(element.tagName === 'IFRAME' ? element.closest('.blankItemDiv') || element : element)).map(element => {
      if (element.tagName !== 'IFRAME') return { element, editor: null, rich: false };
      try {
        const body = element.contentDocument?.body;
        const editor = Object.values(node.ownerDocument.defaultView.UE?.instants || {}).find(item => item.body === body);
        return { element: body, editor, rich: true };
      } catch (_) { return { element: null, editor: null, rich: true }; }
    });
  const blankWritable = field => !!field.element && (field.rich
    ? field.element.isContentEditable && field.editor?.isReady && ['setContent','getContent','sync'].every(key => typeof field.editor[key] === 'function')
    : !field.element.disabled && !field.element.readOnly);
  const blankMatches = (field, expected) => {
    if (!blankWritable(field)) return false;
    if (!field.rich) return field.element.value === expected;
    const normalize = value => value.replace(/\r\n?/g, '\n').replace(/\u00a0/g, ' ').trim();
    if (normalize(field.element.innerText || field.element.textContent || '') !== normalize(expected)) return false;
    // UEditor 的正文和表单提交值必须都一致；不把“看起来写入了”当成同步成功。
    const editor = field.editor, doc = field.element.ownerDocument;
    const backing = typeof editor.textarea === 'object' ? editor.textarea
      : doc.defaultView.parent.document.getElementById('ueditor_textarea_' + editor.options?.textarea);
    const stored = editor.options?.allHtmlEnabled && typeof editor.getAllHtml === 'function'
      ? editor.getAllHtml() : editor.getContent(null, null, true);
    return !!backing && backing.value === stored;
  };
  const text = el => (el?.innerText || el?.textContent || '').trim();
  // 与原脚本 Solver._cleanQuestionText 一致：解码（含双重编码）HTML 实体，
  // 去掉题号、题型标签与分值角标；只用于题干文本，题型判定仍读取原始标签文字。
  const cleanQuestionText = value => {
    let s = String(value || '');
    for (let i = 0; i < 3; i++) {
      if (!/&(quot|apos|lt|gt|amp|nbsp|#\d+|#x[0-9a-f]+);/i.test(s)) break;
      const prev = s;
      s = s.replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&#34;/g, '"')
        .replace(/&#39;/g, "'").replace(/&lt;/g, '<').replace(/&gt;/g, '>')
        .replace(/&nbsp;/g, ' ').replace(/&amp;/g, '&');
      if (s === prev) break;
    }
    const cleaned = s.replace(/\s+/g, ' ')
      .replace(/^[\s\d]+[.、．)]\s*/, '')
      .replace(/^[(（]\s*\d+\s*[)）]\s*/, '')
      .replace(/^【?\s*(单选题|多选题|判断题|填空题|单项选择题|多项选择题|不定项选择题)\s*】?\s*/, '')
      .replace(/^(单选题|多选题|判断题|填空题|单项选择题|多项选择题)[】\]）)]\s*/, '')
      .replace(/[（(]\s*\d+(\.\d+)?\s*分\s*[)）]/g, '')
      .trim();
    // 只由题号/题型标签构成的题干（如「填空题」）清洗后会变空；
    // 保留原文，避免触发既有的「题干为空不提交」门控而误阻合法题目。
    return cleaned || String(value || '').trim();
  };
  const ocrValues = new WeakMap(), ocrIds = new WeakMap(), ocrTargets = new Map();
  let nextOcrId = 0;
  const ocrSource = node => node.nodeType === Node.TEXT_NODE
    ? JSON.stringify([node.textContent, getComputedStyle(node.parentElement).font, document.fonts.status])
    : JSON.stringify([node.getAttribute('src'), node.currentSrc, node.getAttribute('srcset'), node.naturalWidth, node.naturalHeight]);
  const ocrValue = node => {
    const value = ocrValues.get(node);
    return value?.source === ocrSource(node) ? value.text : '';
  };
  // 原脚本在 DOM 提取失败（题干为空或仍是混淆字形）时，会把整题按实际字体渲染后 OCR。
  // 私用区字符即使没有 .font-cxsecret 标记也必须是 OCR 候选，否则该题会永久停在 private-use。
  // 普通汉字仍要求 .font-cxsecret 容器，避免把正常正文全部送去识别。
  const needsFontOcr = node => {
    if (node.nodeType !== Node.TEXT_NODE || !visible(node.parentElement) ||
        node.parentElement.closest('script,style') || ocrValue(node)) return false;
    const chars = [...node.textContent];
    if (node.parentElement.closest('.font-cxsecret'))
      return chars.some(c => /[\u3400-\u9fff\uE000-\uF8FF]/.test(c) && !characterMap[c.codePointAt(0)] && !fallbackCodes.has(c.codePointAt(0)));
    return /[\uE000-\uF8FF]/.test(node.textContent) &&
      chars.some(c => /[\uE000-\uF8FF]/.test(c) && !characterMap[c.codePointAt(0)]);
  };
  const ocrTasks = (limit = 64, skip = []) => {
    for (const [id, node] of ocrTargets) if (!node.isConnected) ocrTargets.delete(id);
    const tasks = [], seen = new Set();
    const add = node => {
      if (seen.has(node) || tasks.length >= Math.min(64, limit) || ocrValue(node)) return;
      seen.add(node);
      if (!ocrIds.has(node)) ocrIds.set(node, 'ocr:' + (++nextOcrId));
      const id = ocrIds.get(node), source = ocrSource(node);
      if (skip.includes(id)) return;
      ocrTargets.set(id, node);
      let data = '', url = '';
      try {
        const canvas = document.createElement('canvas');
        if (node.nodeType === Node.TEXT_NODE) {
          // 逐文本节点渲染实际计算字体，避免整题 OCR 混淆题干与选项。
          const value = node.textContent.trim(), css = getComputedStyle(node.parentElement);
          if (!value || value.length > 1000 || document.fonts.status !== 'loaded') return;
          canvas.width = 1400;
          const font = `${css.fontStyle} ${css.fontWeight} 48px ${css.fontFamily}`;
          const ctx = canvas.getContext('2d'); ctx.font = font;
          const lines = []; let line = '';
          for (const c of value) {
            if (c === '\n' || ctx.measureText(line + c).width > 1340) { lines.push(line); line = c === '\n' ? '' : c; }
            else line += c;
          }
          if (line) lines.push(line);
          canvas.height = Math.max(100, lines.length * 72 + 40);
          ctx.fillStyle = 'white'; ctx.fillRect(0, 0, canvas.width, canvas.height);
          ctx.fillStyle = 'black'; ctx.font = font; ctx.textBaseline = 'top';
          lines.forEach((line, i) => ctx.fillText(line, 20, 20 + i * 72));
        } else {
          url = node.currentSrc || node.src;
          if (!node.complete || !node.naturalWidth || node.naturalWidth * node.naturalHeight > 16000000) return;
          const scale = Math.min(3, Math.max(1, 1500 / node.naturalWidth));
          canvas.width = Math.round(node.naturalWidth * scale); canvas.height = Math.round(node.naturalHeight * scale);
          if (canvas.width * canvas.height > 16000000) return;
          const ctx = canvas.getContext('2d');ctx.fillStyle='white';ctx.fillRect(0,0,canvas.width,canvas.height);
          ctx.drawImage(node, 0, 0, canvas.width, canvas.height);
        }
        data = canvas.toDataURL('image/png');
        if (data.length > 8 * 1024 * 1024) data = '';
      } catch (_) { /* 跨域图片交给原生端下载，字体失败则继续阻止提交。 */ }
      if (data || url) tasks.push({ id, source, data, url });
    };
    for (const root of new Set(questionNodes().flatMap(root => [root, nearestMarkName(root)].filter(Boolean)))) {
      root.querySelectorAll('img').forEach(node => { if (visible(node)) add(node); });
      const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT); let node;
      while ((node = walker.nextNode())) if (needsFontOcr(node)) add(node);
    }
    return tasks;
  };
  const applyOcr = (expectedDocument, id, source, value) => {
    const node = ocrTargets.get(id);
    if (expectedDocument !== documentId || !node?.isConnected || ocrSource(node) !== source || typeof value !== 'string' || !value.trim() || value.length > 12000) return false;
    ocrValues.set(node, { source, text: value.trim() });
    return true;
  };
  const decodedText = el => {
    if (!el) return '';
    const read = current => {
      if (current.nodeType === Node.TEXT_NODE) return ocrValue(current) || (current.parentElement?.closest('.font-cxsecret')
        ? [...current.textContent].map(c => characterMap[c.codePointAt(0)] || c).join('') : current.textContent);
      if (current.nodeType !== Node.ELEMENT_NODE || !visible(current) || current.matches('script,style')) return '';
      const accessible = (current.matches('.font-cxsecret') || current.querySelector('.font-cxsecret')) && current.getAttribute('aria-label')?.trim();
      if (accessible) return accessible;
      if (current.tagName === 'IMG') return ocrValue(current);
      return [...current.childNodes].map(read).join('');
    };
    return read(el).trim();
  };
  const missingFont = node => {
    const coveredOptions = optionPairs(node).filter(pair => pair.accessible).map(pair => pair.element);
    const walker = document.createTreeWalker(node, NodeFilter.SHOW_TEXT);
    let current;
    while ((current = walker.nextNode())) {
      if (!current.parentElement?.closest('.font-cxsecret') || !visible(current.parentElement)) continue;
      if (ocrValue(current)) continue;
      if (coveredOptions.some(root => root.contains(current))) continue;
      const accessible = current.parentElement.closest('[aria-label]');
      if (accessible && node.contains(accessible) && accessible.getAttribute('aria-label').trim()) continue;
      if ([...current.textContent].some(c => /[\u3400-\u9fff\uE000-\uF8FF]/.test(c) && !characterMap[c.codePointAt(0)] && !fallbackCodes.has(c.codePointAt(0)))) return true;
    }
    return false;
  };
  const typeFromText = value => /多选|多项选择|不定项/.test(value) ? 'multiple' : /填空|简答|问答|论述/.test(value) ? 'blank' : /单选|单项选择|判断|是非/.test(value) ? 'single' : '';
  const nearestMarkName = node => {
    const own = node.matches('.mark_name') ? node : node.querySelector('.mark_name');
    if (own) return own;
    for (let current = node, depth = 0; current && depth < 6; current = current.parentElement, depth++) {
      for (let previous = current.previousElementSibling, count = 0; previous && count < 50; previous = previous.previousElementSibling, count++) {
        // 不跨过另一道题借用其标题，避免同文题或分组题串题。
        if (previous.matches(questionSelector) || previous.querySelector(questionSelector)) return null;
        const marks = previous.matches('.mark_name') ? [previous] : [...previous.querySelectorAll('.mark_name')];
        if (marks.length) return marks[marks.length - 1];
      }
    }
    return null;
  };
  const typeHint = node => {
    if (node.querySelector('input[type=checkbox]') && !node.querySelector('input[type=radio]')) return 'multiple';
    const nearby = nearestMarkName(node);
    if (nearby && typeFromText(decodedText(nearby))) return typeFromText(decodedText(nearby));
    for (const label of node.querySelectorAll('.mark_name,.Zy_TItle,.Cy_TItle,.questionType,.question_title,.topic-txt,.stem,.question-title,.quesTitle,.type_tit,h3')) {
      const hint = typeFromText(decodedText(label) + ' ' + (label.getAttribute('aria-label') || '') + ' ' + [...label.querySelectorAll('[aria-label]')].map(el => el.getAttribute('aria-label')).join(' '));
      if (hint) return hint;
    }
    for (let current = node, depth = 0; current && depth < 6; current = current.parentElement, depth++) {
      for (let previous = current.previousElementSibling, count = 0; previous && count < 50; previous = previous.previousElementSibling, count++) {
        const header = previous.matches('.type_tit,.newTestType,h2,h4') ? previous : previous.querySelector('.type_tit,.newTestType');
        const hint = header && typeFromText(decodedText(header));
        if (hint) return hint;
      }
    }
    if (/multipleQuesId/i.test(node.className)) return 'multiple';
    if (/completionQuesId|tiankong|fill/i.test(node.className)) return 'blank';
    if (/judgementQuesId|judge|panduan/i.test(node.className)) return 'single';
    return '';
  };
  const question = (node, index) => {
    const pairs = optionPairs(node);
    const options = pairs.map(pair => (pair.accessible || decodedText(pair.textElement || pair.element)).replace(/^[A-Za-z][.、:：\s]\s*/, '').trim());
    const titleNode = ['h3 .clearfix','h3 div','.Cy_TItle .clearfix','.Zy_TItle .clearfix','.topic-txt','.mark_name','.Zy_TItle','.questionContent','.question_title','.stem','.question-title','.quesTitle','h3','p','h4','.title']
      .map(selector => node.querySelector(selector)).find(el => el && visible(el) && decodedText(el)) || nearestMarkName(node);
    const title = cleanQuestionText(decodedText(titleNode));
    const fields = blanks(node);
    const hint = typeHint(node);
    const kind = hint === 'blank' && fields.length ? 'blank' : options.length ? hint === 'multiple' || (!hint && node.querySelector('[role=checkbox]')) ? 'multiple' : 'single' : fields.length ? 'blank' : 'unsupported';
    // 保存题干、选项和字段数的指纹；内容变化时拒绝旧答案。
    const signature = JSON.stringify([kind, title, options, fields.length]);
    const issue = missingFont(node) || titleNode && missingFont(titleNode) ? 'font' : /[\uE000-\uF8FF]/.test(title) ? 'private-use'
      : [...node.querySelectorAll('img,math')].some(el => visible(el) && (el.tagName !== 'IMG' || !ocrValue(el))) ? 'image' : !title ? 'title' : title.length > 12000 ? 'text-limit' : kind === 'unsupported' || fields.some(field => !blankWritable(field)) ? 'controls' : '';
    return { id: `${questionKey(node, index)}:${signature}`, kind: issue ? 'unsupported' : kind,
      text: title.slice(0, 12000), options, blankCount: fields.length, issue };
  };
  const questionContentToken = () => JSON.stringify(questionNodes().map((node, index) => {
    const q = question(node, index);
    if (!anonymousQuestionIds.has(node)) anonymousQuestionIds.set(node, 'node:' + (++nextQuestionId));
    return [siteQuestionKey(node) || anonymousQuestionIds.get(node), q.kind, q.text, q.options, q.blankCount];
  }));
  const catalog = () => {
    const root = document.querySelector('#coursetree, #courseTree, .posCatalog, .chapter-list, [data-study-catalog]');
    if (!root) return [];
    // 知识点节点：真实页面为 .posCatalog_name[onclick]；离线 fixture 为 li[onclick]。
    const candidates = [...root.querySelectorAll('.posCatalog_name[onclick], li[onclick], a[href*="chapterId"], [role=treeitem][data-id], [data-knowledge-id]')];
    return candidates.filter(node => !candidates.some(other => other !== node && node.contains(other)));
  };
  const complete = node => {
    const row = node.closest('li,[role=treeitem]') || node;
    return row.matches('.finished,.catalog_finished') || !!row.querySelector('.icon-finish,.roundpoint.done,.catalog_finished,.roundpointStudent.done,.icon_Ede_Stuednt,.icon_Completed');
  };
  const active = node => {
    if (node.closest('.posCatalog_active,.currents,[aria-current=page]') || node.querySelector('.posCatalog_active,.currents,[aria-current=page]')) return true;
    // URL chapterId 回退：onclick/href/id 里包含当前 chapterId/knowledgeid 即视为当前节点。
    const m = location.href.match(/[?&](?:chapterId|knowledgeid)=(\d+)/);
    if (m) {
      const id = m[1];
      const src = (node.getAttribute?.('onclick') || '') + (node.getAttribute?.('href') || '') + (node.closest('.posCatalog_select')?.id || '');
      if (src.includes(id)) return true;
    }
    return false;
  };
  let mediaStatus = '';
  let generation = 0;
  let enabled = false;
  let advanced = '';
  let advanceAtPercent = 91;
  let playbackRate = 2;
  const finishedMedia = new Set();
  const clickedTabs = new Set();
  const attemptedPlays = new WeakMap();
  const rateRequests = new WeakMap();
  const mediaSource = v => String(v?.currentSrc || v?.src || v?.getAttribute?.('src') || v?.querySelector?.('source[src]')?.src || '').trim();
  const mediaFinished = v => v.ended || (mediaSource(v) !== '' && finishedMedia.has(mediaSource(v)));
  const finishMedia = v => { const key = mediaSource(v); if (key) finishedMedia.add(key); try { if (!v.paused) pauseMedia(v, 'threshold-pause'); } catch (_) {} };
  // 网站未允许自动播放时，自动定位当前媒体所属播放器中可见的播放按钮并点击。
  // 直接 play() 被站点或浏览器策略拒绝后，驱动网页自身的播放控件更符合其状态机预期。
  const playButtonSelectors = ['.vjs-big-play-button', '.vjs-play-control', 'button[title*="播放"]', '[role="button"][title*="播放"]', 'button[aria-label*="播放"]', '[role="button"][aria-label*="播放"]'];
  const clickPlayButton = media => {
    const container = media.closest('.video-js') || audioPlayerContainer(media) || media.parentElement;
    if (!container) return false;
    for (const selector of playButtonSelectors) {
      for (const button of [...container.querySelectorAll(selector)]) {
        if (!visible(button)) continue;
        // 控制条播放键在播放中会切换为暂停键：仅当播放器状态或文案表明处于暂停时点击。
        const label = `${button.title || ''} ${button.getAttribute('aria-label') || ''}`;
        if (selector === '.vjs-play-control' && !container.classList.contains('vjs-paused') && !/播放|play/i.test(label)) continue;
        recordMedia(media, 'click-play-button');
        button.click();
        return true;
      }
    }
    return false;
  };
  const configure = config => { if (config && Number.isFinite(config.advanceAtPercent)) { const p = Math.round(config.advanceAtPercent); if (p >= 1 && p <= 100) advanceAtPercent = p; } if (config && Number.isFinite(config.playbackRate)) { const r = Number(config.playbackRate); if (r >= 0.25 && r <= 16) playbackRate = r; } return advanceAtPercent; };
  const snapshot = () => {
    const list = mediaElements(), chapters = catalog(), current = chapters.find(active);
    const nodes = questionNodes(), questionOverflow = nodes.length > 1000;
    const url = new URL(location.href);
    const courseId = url.searchParams.get('courseid') || url.searchParams.get('courseId') || document.querySelector('input#courseId,input[name=courseId]')?.value || '';
    const chapterId = url.searchParams.get('chapterId') || url.searchParams.get('knowledgeid') || (current ? text(current) : '');
    const timed = list.filter(v => Number.isFinite(v.duration) && v.duration > 0);
    return { documentId, url: location.href, courseId, chapterId,
      total: chapters.length, completed: chapters.filter(complete).length,
      currentComplete: !!current && complete(current), videoCount: list.filter(v => v.tagName === 'VIDEO').length, videoEnded: list.filter(v => v.tagName === 'VIDEO' && v.ended).length, videoFinished: list.filter(v => v.tagName === 'VIDEO' && mediaFinished(v)).length,
      audioCount: list.filter(v => v.tagName === 'AUDIO').length, audioEnded: list.filter(v => v.tagName === 'AUDIO' && v.ended).length, audioFinished: list.filter(v => v.tagName === 'AUDIO' && mediaFinished(v)).length,
      taskPointCount: taskPoints().length, videoTabCount: mediaTabs().filter(t => /视频|video/i.test(text(t))).length, audioTabCount: mediaTabs().filter(t => !/视频|video/i.test(text(t))).length,
      documentCount: isDocument() ? 1 : 0, documentComplete: isDocument() && documentComplete(),
      duration: timed.length === list.length ? timed.reduce((s, v) => s + v.duration, 0) : 0,
      position: timed.reduce((s, v) => s + Math.min(v.currentTime, v.duration), 0),
      questionTotal: nodes.length, questionOverflow,
      questionPagePending: paperViewPending() || !!pendingQuestionPage && (!nodes.length || questionContentToken() === pendingQuestionPage.beforeContent),
      // 超限时已经阻断自动处理，不再重复解析前 1000 题而耗尽原生读取超时。
      questions: questionOverflow ? [] : nodes.map(question),
      mediaStatus: list.some(v => !mediaFinished(v) && v.error) ? `${mediaLabel(list)}加载失败，请检查网页`
        : list.some(v => !mediaFinished(v) && v.readyState === 0) ? `等待${mediaLabel(list)}加载`
        : list.some(v => !v.paused && !mediaFinished(v) && v.readyState < 3) ? `${mediaLabel(list)}正在缓冲`
        : list.some(v => !v.paused && !v.ended) ? '正在播放' : list.some(v => !mediaFinished(v)) && mediaStatus === '正在播放' ? `${mediaLabel(list)}已暂停` : mediaStatus,
      mediaPaused: list.length > 0 && list.every(v => v.paused), playbackRate, advanceAtPercent, mediaEvents: mediaEvents.slice() };
  };
  const play = () => {
    enabled = true;
    const list = mediaElements();
    // 对应原脚本 _queueVideoSourceRetry：源已设置但元数据未就绪时主动 load() 一次，
    // 由 2 秒轮询继续等待；不重载 iframe，避免丢失 documentId 与题目状态。
    // 必须早于倍速设置，否则 load() 会把 playbackRate 重置为 defaultPlaybackRate。
    for (const v of list) {
      if (mediaFinished(v)) continue;
      const source = mediaSource(v);
      if (source && v.readyState === 0 && v.networkState !== 3 &&
          typeof v.load === 'function' && v.dataset?.cxLoadRequestedSrc !== source) {
        try { v.dataset.cxLoadRequestedSrc = source; v.load(); } catch (_) {}
      }
    }
    // 音视频可能位于子框架；外层导航重绘后也要认出当前媒体标签，避免重新加载它。
    {
      const tabs = taskPoints(), current = tabs.findIndex(taskPointActive);
      if (current >= 0) tabs.slice(0, current + 1).filter(tab => /视频|音频|音訊|audio|video/i.test(text(tab))).forEach(tab => clickedTabs.add(tab));
    }
    list.forEach(v => {
      const key = `${mediaSource(v)}:${playbackRate}`;
      // 同一媒体和设置只申请一次倍速；网页调整后的实际倍速不在轮询中强行覆盖。
      if (rateRequests.get(v) === key) return;
      rateRequests.set(v, key);
      try { if (Number.isFinite(playbackRate) && v.playbackRate !== playbackRate) v.playbackRate = playbackRate; } catch (_) {}
    });
    // 网页可能在客户端接管前已自行播放；先判阈值，不能直接返回 playing。
    let thresholdReached = false;
    for (const v of list) {
      const d = Number(v.duration), t = Number(v.currentTime);
      if (!mediaFinished(v) && v.readyState >= 1 && advanceAtPercent < 100 &&
          Number.isFinite(d) && d > 0 && Number.isFinite(t) && (t / d) * 100 >= advanceAtPercent) {
        finishMedia(v);
        thresholdReached = true;
        mediaStatus = `已播放至 ${advanceAtPercent}%，进入下一任务点`;
      }
    }
    if (list.some(v => !v.paused && !mediaFinished(v))) return 'playing';
    if (thresholdReached && list.every(mediaFinished)) return 'ended';
    const video = list.find(v => !mediaFinished(v));
    if (!video) {
      // 无可见未播放媒体：自动点击下一个「尚未点击过」的音视频标签，避免同一标签被反复点击。
      const vtabs = mediaTabs();
      const next = vtabs.find(t => !clickedTabs.has(t));
      if (next) { clickedTabs.add(next); clickTaskPoint(next); mediaStatus = '已切换到下一个音视频标签'; return 'requested'; }
      return 'ended';
    }
    const source = video.currentSrc || video.src;
    if (!source) return mediaStatus = `等待${mediaLabel(list)}加载`;
    if (video.error) return mediaStatus = `${mediaLabel(list)}加载失败，请检查网页`;
    const previous = attemptedPlays.get(video);
    if (previous?.source === source && previous.epoch === generation) {
      if (previous.pending) return mediaStatus = '正在等待播放器启动';
      // 自动播放被拒后点击过播放按钮仍未起播：继续有限次点击，超限后保留显式人工提示。
      if (previous.blocked && (previous.clicks || 0) < 3) {
        previous.clicks = (previous.clicks || 0) + 1;
        if (clickPlayButton(video)) { mediaStatus = '网站未允许自动播放，已再次点击播放按钮'; return 'requested'; }
      }
      return mediaStatus = previous.blocked
        ? '网站未允许自动播放，多次点击播放按钮仍未起播，请手动点击'
        : `${mediaLabel(list)}已暂停；请点击网页继续或重新开始`;
    }
    const epoch = generation;
    const attempt = { source, epoch, pending: true };
    attemptedPlays.set(video, attempt);
    const onProgress = () => {
      if (epoch !== generation || !enabled || mediaFinished(video)) return;
      const d = Number(video.duration), t = Number(video.currentTime);
      // 100% 等待媒体自然 ended，避免在最后一次 timeupdate 中提前 pause 掉结束事件。
      if (advanceAtPercent < 100 && Number.isFinite(d) && d > 0 && Number.isFinite(t) && (t / d) * 100 >= advanceAtPercent) {
        finishMedia(video);
        mediaStatus = `已播放至 ${advanceAtPercent}%，进入下一任务点`;
      }
    };
    previous?.removeProgress?.();
    video.addEventListener('timeupdate', onProgress);
    attempt.removeProgress = () => video.removeEventListener('timeupdate', onProgress);
    const requestPlay = () => {
      try {
        // 优先走 video.js 播放器自身的播放方法，驱动其状态机；否则回退到原生 play()。
        const audioContainer = audioPlayerContainer(video);
        // 使用已经初始化的音频播放器；对 tech 元素再调用 videojs() 会创建错误实例。
        const player = audioContainer ? window.videojs?.getPlayer?.(audioContainer.id)
          : window.videojs ? window.videojs(video) : null;
        return player && typeof player.play === 'function' ? player.play() : video.play();
      } catch (_) { return video.play(); }
    };
    const start = () => {
      recordMedia(video, 'app-play');
      video.focus({ preventScroll: true });
      let p;
      try { p = requestPlay(); } catch (_) { p = video.play(); }
      return Promise.resolve(p).catch(error => {
        // 对应原脚本 _isAutoplayBlockedError：仅浏览器自动播放限制适合静音重试，
        // 空源/坏源静音没有意义；重试只做一次，仍失败则保留显式提示。
        if (String(error?.name || '') !== 'NotAllowedError') throw error;
        // 与原脚本一致：静音后直接重试元素播放，不重复初始化 video.js 实例。
        video.muted = true;
        recordMedia(video, 'muted-autoplay-retry');
        return video.play();
      }).then(() => {
        attempt.pending = false;
        // 较早的请求不能暂停同一元素上已经开始的新请求。
        if (attemptedPlays.get(video) !== attempt) return;
        if (epoch !== generation || !enabled) { pauseMedia(video, 'late-start-cancel'); return; }
        if (!mediaFinished(video)) mediaStatus = '正在播放';
      });
    };
    start().catch(() => {
      attempt.pending = false;
      if (attemptedPlays.get(video) === attempt && epoch === generation) {
        // 网站未允许自动播放视频：自动找到当前媒体的播放按钮并点击，代替人工干预。
        attempt.blocked = true;
        attempt.clicks = clickPlayButton(video) ? 1 : 0;
        mediaStatus = attempt.clicks
          ? '网站未允许自动播放，已自动点击播放按钮'
          : '播放器未允许自动播放，请点击网页播放按钮';
      }
    });
    return 'requested';
  };
  const stop = () => { playbackProtected = false; enabled = false; generation++; submissionPending = false; fontAbort?.abort(); mediaElements().forEach(v => { try { pauseMedia(v, 'app-stop'); } catch (_) {} }); return true; };
  const advance = () => {
    const state = snapshot();
    if (!state.currentComplete) return 'waiting';
    const key = `${state.courseId}:${state.chapterId}`;
    if (!state.chapterId || advanced === key) return 'waiting';
    const chapters = catalog(), index = chapters.findIndex(active);
    const next = chapters.slice(index + 1).find(node => !complete(node));
    if (index < 0 || !next || !visible(next)) return 'none';
    advanced = key;
    next.click();
    return 'clicked';
  };
  const taskPoints = () => {
    // 只匹配当前任务点顶部的“视频/音频/测验”等任务点标签（.prev_list > .prev_ul > li）。
    // 不再用 [class*="tab"] li，避免把 .tabs 里的目录/讨论/笔记/标注以及整个章节树误当任务点。
    const nodes = [...document.querySelectorAll('.prev_ul li, .prev_tab li, .prev_list li, .chapter_tab li, .ans-cc-tab li')].filter(visible);
    return nodes.filter(el => !nodes.some(other => other !== el && el.contains(other)));
  };
  const mediaTabs = () => {
    const tabs = taskPoints();
    return tabs.filter(t => /视频|音频|音訊|audio|video/i.test(text(t)));
  };
  const taskPointActive = t => t.classList.contains('active') || t.classList.contains('current') || t.classList.contains('on') ||
    t.classList.contains('selected') || t.classList.contains('currents') || t.getAttribute('aria-selected') === 'true' || !!t.closest('.active,.current,.on,.selected,.currents');
  // 平台有些标签把事件绑定在 li 内部的 span/a 上；点击子控件也会冒泡给 li。
  const clickTaskPoint = tab => (tab.querySelector('[onclick],a[href],button,[role=button],[role=tab]') || tab.querySelector('span,div') || tab).click();
  const startVideos = () => {
    const tabs = mediaTabs();
    if (!tabs.length) return 'none';
    // 每次显式开始都从第一个音视频标签起步，后续标签留给顺序推进。
    tabs.forEach(tab => clickedTabs.delete(tab));
    clickedTabs.add(tabs[0]);
    clickTaskPoint(tabs[0]);
    return 'clicked';
  };
  const advanceTaskPoint = () => {
    // 只推进当前任务之后的项目；直接进入/重绘后的当前测验不能再次打开。
    const tabs = taskPoints(), current = tabs.findIndex(taskPointActive);
    const next = tabs.slice(current + 1).find(t => !/视频|音频|音訊|audio|video/i.test(text(t)) && !clickedTabs.has(t));
    if (!next) return 'none';
    clickedTabs.add(next);
    clickTaskPoint(next);
    return 'clicked';
  };
  const submit = () => {
    const candidates = [...document.querySelectorAll('a,button,input[type=button],input[type=submit],[role=button],div[onclick],span[onclick]')].filter(visible);
    const target = candidates.find(el => /^(提交|交卷|提交试卷|确认提交)$/i.test((el.getAttribute?.('value') || text(el) || '').trim()));
    if (!target) return 'none';
    target.click();
    return 'clicked';
  };
  const submittedAnswers = new Set();
  let submissionPending = false;
  const submissionDialogs = () => [...document.querySelectorAll('[role=dialog],[aria-modal=true],dialog[open],.layui-layer,.layui-layer-dialog,.xubox_layer,.ui-dialog,.modal,.modal-dialog,.el-dialog,.ant-modal,.popbox,.popDiv,.popup,.popup-container,.ans-dialog,.confirm-dialog,.alert-dialog,#confirmBox,#dialog')].filter(visible);
  let previousDialogs = new Set();
  const beginSubmissionConfirmation = expectedDocument => {
    if (expectedDocument !== documentId) return false;
    previousDialogs = new Set(submissionDialogs());
    submissionPending = true;
    return true;
  };
  const endSubmissionConfirmation = () => { submissionPending = false; previousDialogs.clear(); return true; };
  let blockedQuestionSignature = '';
  const blockSubmission = () => { blockedQuestionSignature = JSON.stringify(questionNodes().map(question).map(q => q.id)); return true; };
  const verifyAnswers = (expectedDocument, entries) => {
    if (expectedDocument !== documentId) return 'stale';
    const nodes = questionNodes();
    if (blockedQuestionSignature && blockedQuestionSignature === JSON.stringify(nodes.map(question).map(q => q.id))) return 'blocked';
    if (!nodes.length || nodes.length > 1000 || entries.length !== nodes.length) return 'incomplete';
    for (let i = 0; i < nodes.length; i++) {
      const current = question(nodes[i], i), entry = entries[i];
      if (current.kind === 'unsupported' || current.id !== entry.question.id) return 'stale';
      if (current.kind === 'blank') {
        const fields = blanks(nodes[i]);
        if (fields.length !== entry.answer.blanks.length || !fields.every((field, n) => blankMatches(field, entry.answer.blanks[n]))) return 'incomplete';
      } else if (!optionPairs(nodes[i]).every((pair, n) => selected(pair) === entry.answer.choices.includes(n))) return 'incomplete';
    }
    return 'ok';
  };
  const completedQuestionPages = new Map();
  const savedQuestions = new Map();
  const saveQuestionAnswers = entries => {
    const nodes = questionNodes();
    for (let i = 0; i < nodes.length; i++) {
      const entry = entries[i], signature = JSON.stringify(entry.answer);
      if (savedQuestions.get(entry.question.id) === signature) continue;
      const target = [...nodes[i].querySelectorAll('button,a,[role=button],input[type=button],input[type=submit],.saveWork,.save_btn,.saveYl')]
        .find(el => visible(el) && !el.disabled && el.getAttribute('aria-disabled') !== 'true' && !el.matches('.disabled,.readonly') &&
          /^(保存|保存答案|暂存|暂存答案)$/.test((el.value || text(el)).replace(/\s+/g, '')));
      if (!target) continue;
      savedQuestions.set(entry.question.id, signature);
      target.click();
      return true; // 保存可能重绘题目；下一轮重新读回后才继续。
    }
    return false;
  };
  const submitLocalAnswers = entries => {
    const nodes = questionNodes();
    for (let i = 0; i < nodes.length; i++) {
      const entry = entries[i], signature = JSON.stringify(entry.answer);
      if (savedQuestions.get(entry.question.id) === signature) continue;
      const target = [...nodes[i].querySelectorAll('button,a,input[type=button],input[type=submit],[role=button],.jb_btn,.Btn_blue_01,.Btn_blue_1,.saveWork,.save_btn,.submit_btn,.submitBtn,.btnSubmit,.saveYl,.commitBtn')]
        .find(el => visible(el) && !el.disabled && el.getAttribute('aria-disabled') !== 'true' && !el.matches('.disabled,.is-disabled,.readonly,.forbid') &&
          /^(保存|保存答案|暂存|暂存答案|提交|提交答案|确定|完成)$/.test((el.value || text(el)).replace(/\s+/g, '')));
      if (!target) continue;
      savedQuestions.set(entry.question.id, signature);
      submissionPending = true;
      target.click();
      return 'clicked';
    }
    return 'none';
  };
  let pageDefinition = '', pendingQuestionPage = null;
  let fullPaperAttempted = false, pendingPaperView = null;
  const paperViewPending = () => {
    if (pendingPaperView && questionNodes().length && questionContentToken() !== pendingPaperView) pendingPaperView = null;
    return pendingPaperView !== null;
  };
  const prepareQuestionView = () => {
    if (paperViewPending()) return 'waiting';
    if (fullPaperAttempted || !questionNodes().length) return 'none';
    const button = [...document.querySelectorAll('a,button,[role=button],.btn,.Btn')].find(el => visible(el) && !el.disabled && el.getAttribute('aria-disabled') !== 'true' &&
      /^(整卷预览|整卷浏览|查看整卷|整卷模式)$/.test(text(el).replace(/\s+/g, '')));
    if (!button) return 'none';
    fullPaperAttempted = true; pendingPaperView = questionContentToken();
    button.click();
    return 'clicked';
  };
  const questionPages = () => {
    for (const root of document.querySelectorAll(questionPageSelector)) {
      if (!visible(root)) continue;
      const items = new Map();
      for (const candidate of root.querySelectorAll('a,button,li,[role=button],.item,.TiMu-item,.examNum,.quesNum,.examNumItem,.markItem,.examPaperNum-item')) {
        if (!visible(candidate) || !/^\d{1,4}$/.test(text(candidate))) continue;
        const parent = candidate.closest('li');
        const element = parent && root.contains(parent) && text(parent) === text(candidate) ? parent : candidate;
        const key = element.getAttribute('data-question-id') || element.id || text(element);
        const activeNode = element.closest('.active,.current,.cur,.on,.selected,.currents,[aria-current=page],[aria-current=true],[aria-selected=true]') || element.querySelector('.active,.current,.cur,[aria-current=page],[aria-selected=true]');
        items.set(element, { element, key, active: !!activeNode && root.contains(activeNode) });
      }
      if (items.size > 1) return [...items.values()];
    }
    return [];
  };
  const hasUnverifiedPagination = () => [...document.querySelectorAll('button,a,[role=button],input[type=button]')].some(el => visible(el) && /^(下一题|下一道|下一道题|上一题|上一道|上一道题)$/.test((el.value || text(el)).trim()));
  const questionPagesComplete = entries => {
    const pages = questionPages();
    if (!pages.length) return !hasUnverifiedPagination();
    const current = pages.filter(page => page.active);
    return current.length === 1 && !pendingQuestionPage && pages.every(page => completedQuestionPages.has(page.key)) &&
      completedQuestionPages.get(current[0].key) === JSON.stringify(entries.map(entry => entry.question.id));
  };
  const advanceQuestionPage = (expectedDocument, entries) => {
    const verified = verifyAnswers(expectedDocument, entries);
    if (verified !== 'ok') return 'blocked';
    if (saveQuestionAnswers(entries)) return 'saved';
    const pages = questionPages();
    if (!pages.length) return hasUnverifiedPagination() ? 'unverified' : 'none';
    const definition = JSON.stringify(pages.map(page => page.key));
    if (definition !== pageDefinition) { completedQuestionPages.clear(); pendingQuestionPage = null; pageDefinition = definition; }
    const current = pages.filter(page => page.active);
    if (current.length !== 1 || new Set(pages.map(page => page.key)).size !== pages.length) return 'unverified';
    const signature = JSON.stringify(entries.map(entry => entry.question.id));
    if (pendingQuestionPage) {
      if (questionContentToken() === pendingQuestionPage.beforeContent) return 'waiting';
      if (current[0].key !== pendingQuestionPage.key) return 'unverified';
      pendingQuestionPage = null;
    }
    if (completedQuestionPages.has(current[0].key) && completedQuestionPages.get(current[0].key) !== signature)
      completedQuestionPages.clear();
    completedQuestionPages.set(current[0].key, signature);
    const next = pages.find(page => !completedQuestionPages.has(page.key));
    if (!next) return 'done';
    pendingQuestionPage = { key: next.key, beforeContent: questionContentToken() };
    const target = next.element.querySelector('a,button,[role=button],[onclick]') || next.element;
    if (target.disabled || target.getAttribute('aria-disabled') === 'true') { pendingQuestionPage = null; return 'unverified'; }
    // 跨文档翻页会丢失本页核验状态，不能反复从首题开始或提前交卷。
    const href = target.getAttribute('href');
    if (href && !href.startsWith('#') && !/^javascript:/i.test(href)) { pendingQuestionPage = null; return 'unverified'; }
    target.click();
    return 'clicked';
  };
  const submitAnswers = (expectedDocument, entries, localOnly = false) => {
    const verified = verifyAnswers(expectedDocument, entries);
    if (verified !== 'ok') return verified;
    if (localOnly) return submitLocalAnswers(entries);
    if (!questionPagesComplete(entries)) return 'incomplete-pages';
    const signature = JSON.stringify(questionPages().length ? [...completedQuestionPages].sort() : entries.map(e => e.question.id));
    if (submittedAnswers.has(signature)) return 'already';
    const target = [...document.querySelectorAll('button,a,input[type=button],input[type=submit],[role=button],.btn,.Btn,.jb_btn,.Btn_blue_01,.Btn_blue_1,.save_btn,.submit_btn,.el-button,.ant-btn')]
      .filter(visible).find(el => (!questionNodes().some(node => node.contains(el)) || /^(提交作业|提交测验|提交考试|提交试卷|保存并提交|保存提交|交卷)$/.test((el.value || text(el)).replace(/\s+/g, ''))) && !el.disabled && el.getAttribute('aria-disabled') !== 'true' && !el.matches('.disabled,.is-disabled,.forbid,.readonly') &&
        /^(提交|提交答案|提交作业|提交测验|提交考试|提交试卷|保存并提交|保存提交|交卷)$/.test((el.value || text(el)).replace(/\s+/g, '')));
    if (!target) return 'none';
    submittedAnswers.add(signature);
    submissionPending = true;
    target.click();
    return 'clicked';
  };
  const confirmSubmission = () => {
    if (!submissionPending) return false;
    const roots = submissionDialogs().filter(root => !previousDialogs.has(root));
    for (const root of roots) {
      if (!/提交|交卷/.test(text(root))) continue;
      if (/未作答|未答|漏答|未完成|未填写/.test(text(root))) { submissionPending = false; return 'blocked'; }
      const button = [...root.querySelectorAll('button,a,input[type=button],input[type=submit],[role=button],div[onclick],span[onclick],.popbtn,.sure,.confirm')].filter(visible)
        .find(el => !el.disabled && el.getAttribute('aria-disabled') !== 'true' && /^(确定|确认|确认提交|继续提交|提交|提交答案|提交作业|提交测验|提交试卷|交卷)$/.test((el.value || text(el)).replace(/\s+/g, '')));
      if (button) { submissionPending = false; button.click(); return true; }
    }
    return false;
  };
  const submissionUnresolved = () => submissionPending && submissionDialogs().some(root => !previousDialogs.has(root));
  const documentMarker = () => document.querySelector('.ans-doc, .ans-ppt, .pdf-viewer, .ppt-viewer, .document-reader, .pdfViewer, .document-viewer');
  const documentUrl = () => /(?:\/ananas\/modules\/(?:pdf|ppt|doc|book)\/|\/screen\/v2\/|\.(?:pdf|ppt|pptx|doc|docx)(?:[?#]|$))/i.test(location.href);
  // 网盘入口可能先到 /mooc-ans/screen/file，再重定向到 /screen/v2/。
  // iframe 的 src 保留入口地址；外层须交给内层处理，不能一直等待外层翻页。
  const isDocument = () => (!!documentMarker() || documentUrl()) &&
    ![...document.querySelectorAll('iframe[src]')].some(frame => visible(frame) &&
      /(?:\/screen\/v2\/|\/mooc-ans\/screen\/file(?:[/?#]|$)|\/ananas\/modules\/(?:pdf|ppt)\/)/i.test(frame.getAttribute('src') || ''));
  const documentSlides = () => {
    const nodes = [...document.querySelectorAll('.swiper-wrapper .swiper-slide, .ppt-page-list li, .ppt-slide-list li, .slide-list li, .thumb-list li, .thumbnail-list li, .page-list li, [class*="slide-list"] li, [class*="page-list"] li')].filter(visible);
    return nodes.filter(el => !nodes.some(other => other !== el && el.contains(other))).slice(0, 500);
  };
  const slideActive = s => /(?:^|\s)(?:active|is-active|current|on|swiper-slide-active)(?:\s|$)/i.test(String(s.className || '')) || /^(?:true|page)$/.test(String(s.getAttribute?.('aria-current') || ''));
  const documentPage = () => {
    const slides = documentSlides();
    if (slides.length > 1) { const idx = slides.findIndex(slideActive); return { current: idx < 0 ? 1 : idx + 1, total: slides.length }; }
    const match = (text(document.querySelector('#pageNumber, .pdf-page-number, [class*="pageNum"], [class*="page-num"]')) || '').match(/(\d+)\s*\/\s*(\d+)/);
    return match ? { current: parseInt(match[1], 10), total: parseInt(match[2], 10) } : { current: 0, total: 0 };
  };
  const documentScrollState = new WeakMap();
  const documentScrollTarget = () => {
    if (!isDocument()) return null;
    const root = documentMarker();
    const candidates = root ? [root, ...root.querySelectorAll('*')] : [...document.querySelectorAll('*')];
    if (documentUrl() && document.scrollingElement) candidates.push(document.scrollingElement);
    return [...new Set(candidates)].filter(el => {
      if (!visible(el) || el.clientWidth < 160 || el.clientHeight < 100 || el.scrollHeight <= el.clientHeight + 2) return false;
      if (el.closest('aside,nav,.sidebar,.thumbnail-list,.thumb-list,.slide-list,.ppt-page-list,.ppt-slide-list')) return false;
      return el === document.scrollingElement || /^(auto|scroll)$/.test(getComputedStyle(el).overflowY);
    }).sort((a, b) => b.clientWidth * b.clientHeight - a.clientWidth * a.clientHeight)[0] || null;
  };
  const documentScrollComplete = el => {
    const bottom = el.scrollHeight - el.clientHeight - el.scrollTop <= 2;
    let state = documentScrollState.get(el);
    if (!state || state.height !== el.scrollHeight || !bottom) {
      state = { height: el.scrollHeight, bottomAt: bottom ? performance.now() : null };
      documentScrollState.set(el, state);
    }
    if (bottom && state.bottomAt === null) state.bottomAt = performance.now();
    // 给滚动事件和延迟加载留出时间；内容高度增长后重新滚动、重新计时。
    return bottom && performance.now() - state.bottomAt >= 2000;
  };
  const documentComplete = () => {
    const scroll = documentScrollTarget();
    if (scroll) return documentScrollComplete(scroll);
    const page = documentPage();
    return page.total > 0 && page.current >= page.total;
  };
  const advanceDocument = () => {
    if (!isDocument()) return 'none';
    const scroll = documentScrollTarget();
    if (scroll) {
      if (documentScrollComplete(scroll)) return 'done';
      if (scroll.scrollHeight - scroll.clientHeight - scroll.scrollTop > 2) {
        scroll.scrollTop = scroll.scrollHeight;
        documentScrollComplete(scroll);
        return 'scrolled';
      }
      return 'waiting';
    }
    const progress = documentPage();
    if (progress.total > 0 && progress.current >= progress.total) return 'done';
    const next = [...document.querySelectorAll('a,button,div[onclick],span[onclick],[role=button]')].filter(visible)
      .find(b => /^(下一页|下页|下一张|下一屏|后一页|NEXT)$/i.test((text(b) || b.getAttribute?.('aria-label') || b.getAttribute?.('title') || '').trim()));
    if (next) { next.click(); return 'clicked'; }
    const slides = documentSlides();
    if (slides.length > 1) {
      const idx = slides.findIndex(slideActive);
      const target = slides[idx < 0 ? 1 : idx + 1];
      if (target) { (target.querySelector('button, a, [role="button"], img') || target).click(); return 'clicked'; }
    }
    return 'none';
  };
  const fill = (expectedDocument, expected, answer) => {
    if (expectedDocument !== documentId) return false;
    const node = questionNodes().find((n, i) => question(n, i).id === expected.id);
    if (!node) return false;
    const current = question(node, questionNodes().indexOf(node));
    if (current.kind === 'unsupported' || current.kind !== expected.kind) return false;
    const choices = optionPairs(node), fields = blanks(node);
    if (current.kind === 'blank') {
      if (answer.blanks.length !== fields.length || answer.blanks.some(s => !s.trim())) return false;
      if (fields.some(field => !blankWritable(field))) return false;
      try {
        fields.forEach((field, i) => {
          if (blankMatches(field, answer.blanks[i])) return;
          const input = field.element, owner = input.ownerDocument.defaultView;
          if (field.rich) {
            const escaped = input.ownerDocument.createElement('div');
            escaped.textContent = answer.blanks[i];
            field.editor.setContent(escaped.innerHTML.replace(/\r?\n/g, '<br>'));
            field.editor.sync();
          } else {
            const proto = input.tagName === 'TEXTAREA' ? owner.HTMLTextAreaElement.prototype : owner.HTMLInputElement.prototype;
            Object.getOwnPropertyDescriptor(proto, 'value').set.call(input, answer.blanks[i]);
          }
          input.dispatchEvent(new owner.Event('input', { bubbles: true }));
          input.dispatchEvent(new owner.Event('change', { bubbles: true }));
        });
        return fields.every((field, i) => blankMatches(field, answer.blanks[i]));
      } catch (_) { return false; }
    }
    if (!answer.choices.length || answer.choices.some(i => !Number.isInteger(i) || i < 0 || i >= choices.length) ||
        (current.kind === 'single' && answer.choices.length !== 1)) return false;
    if (choices.some(pair => pair.input?.disabled || pair.element.getAttribute('aria-disabled') === 'true')) return false;
    choices.forEach((pair, i) => {
      const wanted = answer.choices.includes(i);
      if (selected(pair) !== wanted && (wanted || current.kind === 'multiple'))
        (pair.input || pair.element.querySelector('[onclick],a[href],button,[role=button],.after') || pair.element).click();
    });
    return choices.every((pair, i) => selected(pair) === answer.choices.includes(i));
  };
  const fontBuffers = new Map();
  let fontUrl = '';
  const fontDocuments = () => {
    const docs = [document];
    // 原脚本搜索题目文档及其他可访问文档；同时支持字体位于父页面。
    try { for (let w = window.parent; w !== window; w = w.parent) { if (!docs.includes(w.document)) docs.push(w.document); if (w === w.parent) break; } } catch (_) {}
    for (let i = 0; i < docs.length && i < 32; i++)
      for (const frame of docs[i].querySelectorAll('iframe')) {
        try { if (frame.contentDocument && !docs.includes(frame.contentDocument)) docs.push(frame.contentDocument); } catch (_) {}
      }
    return docs;
  };
  const findFontUrl = () => {
    for (const doc of fontDocuments()) {
      const urls = [], visited = new Set();
      // 混淆标记的 CSS 类名固定，实际 @font-face 家族名可能由页面动态生成。
      const normalizeFamily = value => value.trim().replace(/^['"]|['"]$/g, '').toLowerCase();
      const usedFamilies = new Set([...doc.querySelectorAll('.font-cxsecret')]
        .flatMap(el => doc.defaultView.getComputedStyle(el).fontFamily.split(',').map(normalizeFamily)));
      const scan = (sheet, base, depth = 0) => {
        if (!sheet || visited.has(sheet) || depth > 8) return;
        visited.add(sheet);
        try { for (const rule of sheet.cssRules || []) {
          if (rule.type === 5 && (/cxsecret/i.test(rule.cssText) || usedFamilies.has(normalizeFamily(rule.style.getPropertyValue('font-family'))))) {
            const match = rule.cssText.match(/url\(\s*['"]?([^'"\s)]+)['"]?\s*\)/i);
            if (match) urls.push(new URL(match[1], base).href);
          } else if (rule.styleSheet) scan(rule.styleSheet, rule.styleSheet.href || base, depth + 1);
          else if (rule.cssRules) scan(rule, base, depth + 1);
        } } catch (_) {}
      };
      for (const sheet of doc.styleSheets) scan(sheet, sheet.href || doc.baseURI);
      if (urls.length) { fontSourceCount = new Set(urls).size; return urls[0]; }
    }
    fontSourceCount = 0;
    return '';
  };
  const parseFontBuffer = (buffer, signal) => {
        const parser = window.__studyFontParser;
        fontStage = 'parse';
        const font = parser.parse(new Uint8Array(buffer))[0];
        const codes = [...new Set(questionNodes().flatMap(node => [node, ...node.querySelectorAll('.font-cxsecret')].filter(el => el.closest('.font-cxsecret')).flatMap(el => [...text(el)])))]
          .map(c => c.codePointAt(0)).filter(code => code >= 0x3400 && code <= 0xF8FF).slice(0, 2048);
        const paths = {};
        fallbackCodes = new Set();
        fontStage = 'glyphs';
        for (const code of codes) {
          if (signal.aborted) return;
          const glyph = parser.U.codeToGlyph(font, code);
          // 字体 cmap 未收录的普通汉字由浏览器使用后备字体显示，无须替换。
          // 私用区字符以及有字形但未匹配的字符仍必须阻止发送。
          if (!glyph) { if (code <= 0x9fff) fallbackCodes.add(code); continue; }
          const path = parser.U.glyphToPath(font, glyph);
          if (path?.cmds?.length) paths[code] = JSON.stringify(path);
        }
        fontState = { status: 'ready', documentId, paths };

  };
  const parseFontBytes = (expectedDocument, encoded) => {
    if (expectedDocument !== documentId || fontAbort?.signal.aborted) return false;
    try {
      const data = atob(encoded), bytes = Uint8Array.from(data, c => c.charCodeAt(0));
      if (bytes.byteLength > 8 * 1024 * 1024) throw new Error('font-too-large');
      fontBuffers.set(fontUrl, bytes.buffer);
      parseFontBuffer(bytes.buffer, fontAbort.signal);
      return true;
    } catch (_) { fontState = { status: 'failed', documentId, error: 'font-parse-failed' }; return false; }
  };
  const prepareFont = () => {
    if (fontState.status === 'loading') return 'loading';
    fontAbort = new AbortController();
    const signal = fontAbort.signal;
    fontState = { status: 'loading', documentId };
    (async () => {
      try {
        fontStage = 'source';
        const source = findFontUrl();
        if (!source) throw new Error('font-source-unresolved');
        if (source !== fontUrl) { characterMap = {}; fallbackCodes = new Set(); }
        fontUrl = source;
        const url = new URL(source);
        if (!(url.protocol === 'data:' || (url.protocol === 'https:' &&
            (url.hostname === 'study-desktop.local' || /(^|\.)(chaoxing|xuexitong)\.com$/.test(url.hostname))))) throw new Error('font-host-unsupported');
        fontStage = 'download';
        let buffer = fontBuffers.get(source);
        if (!buffer) {
          const response = await fetch(url.href, { signal, credentials: 'same-origin', redirect: 'error' });
          if (!response.ok) throw new Error('font-download-failed');
          buffer = await response.arrayBuffer();
          if (buffer.byteLength > 8 * 1024 * 1024) throw new Error('font-too-large');
          if (fontBuffers.size >= 2) fontBuffers.clear();
          fontBuffers.set(source, buffer);
        }
        if (!signal.aborted) parseFontBuffer(buffer, signal);
      } catch (error) {
        const known = ['font-source-unresolved', 'font-host-unsupported', 'font-download-failed', 'font-too-large'];
        fontState = { status: 'failed', documentId, url: fontStage === 'download' ? fontUrl : '',
          error: error.name === 'AbortError' ? 'cancelled' : known.includes(error.message) ? error.message
            : fontStage === 'download' ? 'font-download-failed' : 'font-parse-failed' };
      }
    })();
    return 'loading';
  };  const applyFontMap = (expectedDocument, map) => {
    if (expectedDocument !== documentId) return false;
    characterMap = map;
    fontStage = 'mapped';
    fontState = { status: 'applied', documentId };
    return true;
  };
  // 仅输出状态、数量与控件结构，不包含题干、答案、字体地址或字形内容。
  const diagnostics = () => ({
    font: { status: fontState.status, stage: fontStage, error: fontState.error || '',
      sourceCount: fontSourceCount, mappedCount: Object.keys(characterMap).length, fallbackCount: fallbackCodes.size,
      glyphCount: Object.keys(fontState.paths || {}).length },
    questions: questionNodes().map(node => ({
      classes: node.className,
      editorFrames: [...node.querySelectorAll('iframe')].map(frame => {
        try { const doc = frame.contentDocument; return { readable: !!doc?.body, editable: !!doc?.body?.isContentEditable,
          designMode: doc?.designMode, managed: Object.values(window.UE?.instants || {}).filter(editor => editor.body === doc?.body).length }; }
        catch (_) { return { readable: false }; }
      }),
      controls: [...node.querySelectorAll('input,textarea,li,[role],a,span,label,div,[contenteditable]')].slice(0, 100).map(el => ({
        tag: el.tagName, classes: el.className, type: el.getAttribute('type'), role: el.getAttribute('role'),
        clickHandler: !!el.getAttribute('onclick'), editable: el.isContentEditable
      }))
    }))
  });
  const dumpTabs = () => {
    const broad = [...document.querySelectorAll('li[onclick],div[onclick],span[onclick],a[onclick],button,[role=tab],li,a,[class*="tab"],[class*="catalog"],[class*="task"],[class*="job"],[class*="ans-"],[class*="prev_"],.posCatalog,[class*="chapter"]')].filter(visible);
    const dedup = broad.filter((el, i, arr) => !arr.some((o, j) => j !== i && o !== el && o.contains(el))).slice(0, 300);
    return {
      url: location.href,
      documentReader: { detected: isDocument(), complete: isDocument() && documentComplete(),
        scrollables: [...document.querySelectorAll('*')].filter(el => visible(el) && el.clientHeight >= 100 && el.scrollHeight > el.clientHeight + 2)
          .slice(0, 25).map(el => ({ tag: el.tagName, id: el.id, classes: String(el.className || '').slice(0, 120),
            height: el.clientHeight, width: el.clientWidth, contentHeight: el.scrollHeight, top: el.scrollTop, overflow: getComputedStyle(el).overflowY })) },
      questionStructures: diagnostics().questions,
      questionSummary: snapshot().questions.map(q => ({ kind: q.kind, issue: q.issue, blankCount: q.blankCount, optionCount: q.options.length })),
      fontSources: fontDocuments().map(doc => ({
        host: new URL(doc.baseURI).hostname,
        sheets: [...doc.styleSheets].map(sheet => {
          try { return { href: sheet.href, rules: [...sheet.cssRules].filter(r => /font-face|cxsecret/i.test(r.cssText)).map(r => r.cssText.slice(0, 500)) }; }
          catch (_) { return { href: sheet.href, blocked: true }; }
        }),
        secretFamilies: [...new Set([...doc.querySelectorAll('.font-cxsecret')].map(el => doc.defaultView.getComputedStyle(el).fontFamily))],
        loadedFaces: [...doc.fonts].map(face => ({family:face.family,status:face.status})),
        fontResources: doc.defaultView.performance.getEntriesByType('resource').filter(r => /\.(woff2?|ttf|otf)(\?|$)/i.test(r.name)).map(r => r.name)
      })),
      matchedTaskPoints: taskPoints().map(t => ({ tag: t.tagName, classes: String(t.className || ''), text: text(t).slice(0, 40), parent: String(t.parentElement?.className || ''), gp: String(t.parentElement?.parentElement?.className || '') })),
      matchedVideoTabs: mediaTabs().filter(t => /视频|video/i.test(text(t))).map(t => text(t).slice(0, 40)),
      matchedAudioTabs: mediaTabs().filter(t => !/视频|video/i.test(text(t))).map(t => text(t).slice(0, 40)),
      catalogStats: { total: catalog().length, completed: catalog().filter(complete).length },
      videoInfo: {
        videoCount: mediaElements().filter(v => v.tagName === 'VIDEO').length,
        audioCount: mediaElements().filter(v => v.tagName === 'AUDIO').length,
        first: mediaElements()[0] ? {
          src: (mediaElements()[0].currentSrc || mediaElements()[0].src || '').slice(0, 160),
          readyState: mediaElements()[0].readyState, paused: mediaElements()[0].paused, ended: mediaElements()[0].ended,
          duration: String(mediaElements()[0].duration), currentTime: String(mediaElements()[0].currentTime),
          networkState: mediaElements()[0].networkState, error: mediaElements()[0].error ? mediaElements()[0].error.code : null,
          muted: mediaElements()[0].muted, autoplay: mediaElements()[0].autoplay, preload: mediaElements()[0].preload, controls: mediaElements()[0].controls
        } : null
      },
      player: {
        html: (document.querySelector('video,audio')?.parentElement?.outerHTML || document.body?.innerHTML || '').slice(0, 2500),
        clickables: [...document.querySelectorAll('[onclick],button,.vjs-play-control,.vjs-big-play-button,[class*="play"],[class*="Play"],[class*="vjs"]')].filter(visible).slice(0, 30).map(el => ({
          tag: el.tagName, id: el.getAttribute?.('id') || '', classes: String(el.className || '').slice(0, 90),
          text: text(el).slice(0, 30), onclick: (el.getAttribute?.('onclick') || '').slice(0, 100)
        }))
      },
      html: {
        prev_box: (document.querySelector('.prev_box')?.outerHTML || '').slice(0, 2000),
        tabs: (document.querySelector('.tabs')?.outerHTML || '').slice(0, 2000)
      },
      coursetree: {
        html: (document.querySelector('#coursetree')?.innerHTML || '').slice(0, 6000),
        items: [...document.querySelectorAll('#coursetree li, #coursetree .posCatalog_select, #coursetree .posCatalog_level')].slice(0, 60).map(li => ({
          tag: li.tagName, classes: String(li.className || ''), text: text(li).slice(0, 50),
          onclick: (li.getAttribute?.('onclick') || '').slice(0, 140), id: li.getAttribute?.('id') || '',
          done: /(?:^|\s)(finished|catalog_finished)(?:\s|$)/i.test(String(li.className || '')) || !!li.querySelector('.roundpoint.done,.icon-finish,.catalog_finished,.roundpointStudent.done,.icon_Ede_Stuednt')
        }))
      },
      candidates: dedup.map(el => ({
        tag: el.tagName, classes: String(el.className || '').slice(0, 140), text: text(el).slice(0, 40),
        onclick: (el.getAttribute?.('onclick') || '').slice(0, 140), role: el.getAttribute?.('role') || '',
        active: /(?:^|\s)(active|current|on|selected|currents)(?:\s|$)/i.test(String(el.className || ''))
      })).filter(c => c.text || c.onclick || /tab|catalog|task|job|ans-|prev_|chapter|posCatalog/i.test(c.classes))
    };
  };
  // ---- 课程页面 / 学习页面判定与课程任务点总览 ----
  // 课程页面 = 用户已在当前窗口打开的、带各个任务点的章节目录界面；
  // 学习页面是章节内容（音视频/题目/文档，多为子框架），不冒充课程总览。
  // 课程入口只用于识别「课程列表页」，不用于导航：流程不会获取或重新进入课程页面。
  const courseLinkElements = () => {
    const nodes = [...document.querySelectorAll(
      'a[href*="courseid"],a[href*="courseId"],a[href*="clazzid"],a[href*="studentstudy"],a[href*="/course/"],' +
      '[data-courseid],[data-course-id],[onclick*="courseid"],[onclick*="clazzid"],' +
      '.courseItem,.course-item,.courseBox,.course-card'
    )].filter(visible);
    // 只保留最外层入口，避免同一课程卡片的内层元素被重复计入。
    return nodes.filter(el => !nodes.some(other => other !== el && other.contains(el)))
      .filter(el => {
        const hint = (el.getAttribute('href') || '') + ' ' + (el.getAttribute('onclick') || '') +
          ' ' + (el.getAttribute('data-courseid') || '') + ' ' + (el.getAttribute('data-course-id') || '');
        return /(?:courseid|clazzid|studentstudy|\/course\/)/i.test(hint) || el.matches('.courseItem,.course-item,.courseBox,.course-card');
      }).slice(0, 20);
  };
  const courseLinkNodes = () => courseLinkElements().map(el => {
    const anchor = el.matches('a[href]') ? el : el.querySelector('a[href]');
    const href = anchor && /^https?:/i.test(anchor.href) ? anchor.href : location.href;
    const label = (text(el) || (anchor ? text(anchor) : '')).slice(0, 120);
    return { label: label || '课程', href: /^https?:/i.test(href) ? href : '' };
  });
  const courseName = () => {
    for (const el of document.querySelectorAll('.courseName,#courseName,.course-name,.course_title,.courseTitle,[data-course-name],h1.course,.course-info h1,.coursename,.course_name')) {
      const value = text(el);
      if (value && value.length <= 120) return value;
    }
    const title = (document.title || '').trim();
    return title ? title.replace(/\s*[-_|]\s*(?:学习通|超星|泛雅).*$/, '').trim().slice(0, 120) : '';
  };
  const courseKey = () => {
    const url = new URL(location.href);
    const id = url.searchParams.get('courseid') || url.searchParams.get('courseId') ||
      document.querySelector('input#courseId,input[name=courseId]')?.value || '';
    const clazz = url.searchParams.get('clazzid') || url.searchParams.get('classId') || '';
    return [id, clazz].filter(Boolean).join('/') || '';
  };
  const courseTasks = () => catalog().map((node, index) => ({
    index, label: text(node).slice(0, 120), completed: complete(node)
  }));
  // 课程页面的全部任务点来自章节目录树；学习页面只报告自身载体，不冒充课程总览。
  const courseOverview = () => {
    const chapters = catalog();
    const studyMarkers = taskPoints().length + mediaElements().length + questionNodes().length +
      document.querySelectorAll('.ans-attach-ct,.ans-doc,.ans-job,.document-reader,.pdfViewer,.pptViewer').length;
    const links = courseLinkNodes();
    const pageKind = chapters.length ? 'course' : studyMarkers ? 'study' : links.length ? 'catalog' : 'other';
    const tasks = courseTasks();
    return {
      documentId, url: location.href, pageKind, courseKey: courseKey(), courseName: courseName(),
      taskTotal: tasks.length, taskCompleted: tasks.filter(t => t.completed).length,
      firstPendingIndex: tasks.findIndex(t => !t.completed),
      tasks: tasks.slice(0, 300), courseLinks: links, studyMarkers
    };
  };
  // 只点击目录树中指定序号的任务点；不重载页面，保留已登录会话与其它章节状态。
  const openCourseTask = index => {
    const node = catalog()[index];
    if (!node) return 'none';
    const control = node.matches('[onclick],a[href]') ? node :
      node.querySelector('[onclick],a[href],[role=button],.posCatalog_name') || node;
    try { control.click(); return 'clicked'; } catch (_) { return 'none'; }
  };
  window.__studyDesktop = { documentId, snapshot, blockSubmission, submissionUnresolved, prepareQuestionView, ocrTasks, applyOcr, configure, setPlaybackProtection, play, startVideos, stop, advance, advanceTaskPoint, advanceDocument, submit, submitAnswers, advanceQuestionPage, beginSubmissionConfirmation, endSubmissionConfirmation, confirmSubmission, fill, prepareFont, parseFontBytes, fontResult: () => fontState, applyFontMap, diagnostics, dumpTabs, courseOverview, openCourseTask };
})();
