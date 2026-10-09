'use strict';
(() => {
  const $ = id => document.getElementById(id);
  const icons = {
    volume: '<path d="m11 4-6 5H2v6h3l6 5zM15 8a6 6 0 0 1 0 8M18 5a10 10 0 0 1 0 14"/>',
    muted: '<path d="m11 4-6 5H2v6h3l6 5zM16 9l6 6M22 9l-6 6"/>',
    back: '<path d="m14 5-7 7 7 7"/>', forward: '<path d="m10 5 7 7-7 7"/>',
    reload: '<path d="M20 7v5h-5M20 12a8 8 0 1 0-2 6"/>', close: '<path d="m6 6 12 12M18 6 6 18"/>',
    home: '<path d="m3 10 9-7 9 7M5 9v12h5v-7h4v7h5V9"/>',
    search: '<circle cx="10.5" cy="10.5" r="6.5"/><path d="m16 16 5 5"/>',
    lock: '<rect x="5" y="10" width="14" height="11" rx="3"/><path d="M8 10V7a4 4 0 0 1 8 0v3"/>',
    shield: '<path d="m12 3 8 3v6c0 5-8 9-8 9s-8-4-8-9V6z"/>',
    star: '<path d="m12 3 2.8 5.7 6.2.9-4.5 4.4 1.1 6.2-5.6-2.9-5.6 2.9 1.1-6.2L3 9.6l6.2-.9z"/>',
    plus: '<path d="M12 5v14M5 12h14"/>', minus: '<path d="M5 12h14"/>',
    maximize: '<rect x="5" y="5" width="14" height="14" rx="2"/>',
    grid: '<rect x="4" y="4" width="6" height="6" rx="1"/><rect x="14" y="4" width="6" height="6" rx="1"/><rect x="4" y="14" width="6" height="6" rx="1"/><rect x="14" y="14" width="6" height="6" rx="1"/>',
    edit: '<path d="m4 16 12-12 4 4L8 20H4zM14 6l4 4"/>',
    globe: '<circle cx="12" cy="12" r="9"/><ellipse cx="12" cy="12" rx="4" ry="9"/><path d="M3 12h18"/>',
    pin: '<path d="m8 3 8 0-1 7 3 3v2H6v-2l3-3zM12 15v6"/>'
  };
  const svg = name => `<svg viewBox="0 0 24 24" aria-hidden="true">${icons[name] || icons.globe}</svg>`;
  document.querySelectorAll('[data-icon]').forEach(el => { el.innerHTML = svg(el.dataset.icon); });
  const native = !!(window.chrome?.webview || window.webkit?.messageHandlers?.veyra);
  const read = (key, fallback) => { try { return JSON.parse(localStorage.getItem(key)) ?? fallback; } catch { return fallback; } };
  const write = (key, value) => { try { localStorage.setItem(key, JSON.stringify(value)); if(window.webkit?.messageHandlers?.veyra) send('preference',JSON.stringify({key,value})); } catch { toast('Preferences could not be saved.'); } };
  let state = { tabs: [{id:'preview',title:'New tab',home:true}], selected:'preview', address:'', home:true, canBack:false, canForward:false, loading:false, ready:true, favorites:[], capture:'unavailable', message:'' };
  let theme = read('veyra.theme', 'system');
  if (!['light','dark','system'].includes(theme)) theme = 'system';
  document.documentElement.dataset.theme = theme;
  const defaults = [
    ['Google','https://www.google.com'], ['YouTube','https://www.youtube.com'], ['Gmail','https://mail.google.com'], ['Maps','https://maps.google.com'],
    ['Drive','https://drive.google.com'], ['Calendar','https://calendar.google.com'], ['GitHub','https://github.com'], ['Wikipedia','https://wikipedia.org']
  ].map(([title,url]) => ({title,url}));
  const safeURL = value => { try { const url = new URL(value); return ['http:','https:'].includes(url.protocol) && !!url.hostname ? url.href : null; } catch { return null; } };
  let shortcuts = read('veyra.shortcuts', defaults);
  if (!Array.isArray(shortcuts) || shortcuts.length !== 8 || shortcuts.some(s => typeof s?.title !== 'string' || !safeURL(s.url))) shortcuts = defaults;
  let history = read('veyra.history', []);
  if (!Array.isArray(history)) history = [];
  history = history.filter(h => typeof h?.title === 'string' && safeURL(h.url)).slice(0, 50);
  const observedVisits = new Map();
  let panelOpen = false, suggestionOpen = false, activeSuggestion = -1, matches = [], focusReturn = null, lastMessage = '', statusTimer;
  function send(action, value = '') {
    const message = {action, value: String(value)};
    if (window.chrome?.webview) window.chrome.webview.postMessage(message);
    else if (window.webkit?.messageHandlers?.veyra) window.webkit.messageHandlers.veyra.postMessage(message);
    else preview(message);
  }
  function preview({action,value}) {
    const previous=state;state={...state,tabs:state.tabs.map(t=>({...t}))};
    if (action === 'newTab') { if (state.tabs.length >= 12) {state=previous;return toast('A maximum of 12 tabs can be open.');} const id = String(Date.now()); state.tabs.push({id,title:'New tab',home:true}); state.selected=id; }
    if (action === 'selectTab') state.selected=value;
    if (action === 'closeTab' && state.tabs.length > 1) { state.tabs=state.tabs.filter(t=>t.id!==value); if (state.selected===value) state.selected=state.tabs[0].id; }
    if (action === 'navigate') { const tab=state.tabs.find(t=>t.id===state.selected); tab.home=false; tab.title=value; state.address=value; toast('Preview: navigation is available in the desktop app.'); }
    if (action === 'home') state.tabs.find(t=>t.id===state.selected).home=true;
    state.home=state.tabs.find(t=>t.id===state.selected)?.home ?? true;
    const next=state;state=previous;render(next);
  }
  function overlay() { if(!panelOpen&&!suggestionOpen){$('page-preview').hidden=true;$('page-preview').removeAttribute('src');}send('overlay', panelOpen || suggestionOpen ? 'true' : 'false'); }
  function toast(message) { $('status').textContent=message; $('status').hidden=false; clearTimeout(statusTimer); statusTimer=setTimeout(()=>{$('status').hidden=true;},4000); }
  function render(next) {
    const previous = state;
    state = next;
    if(previous.selected!==state.selected){closeSuggestions();closePanel();}
    if(state.home||state.error){$('page-preview').hidden=true;$('page-preview').removeAttribute('src');}
    renderTabs();
    $('back').disabled=!state.canBack; $('forward').disabled=!state.canForward;
    $('reload').disabled=!!state.home || !state.ready;
    $('reload').innerHTML=svg(state.loading?'close':'reload'); $('reload').setAttribute('aria-label',state.loading?'Stop loading':'Reload page');
    $('mute').disabled=!state.canMute || !!state.home;
    $('mute').innerHTML=svg(state.muted?'muted':'volume');
    $('mute').setAttribute('aria-label',state.canMute?(state.muted?'Unmute tab audio':'Mute tab audio'):'Tab audio control unavailable on this platform');
    $('bookmark').disabled=!!state.home || !state.ready;
    $('bookmark').classList.toggle('saved',state.favorites.some(f=>f.url===state.address));
    if (previous.selected!==state.selected || document.activeElement !== $('address')) $('address').value=state.home?'':state.address;
    const secure = state.address?.startsWith('https://');
    $('security').innerHTML=svg(state.home?'search':secure?'lock':'globe');
    $('security').title=state.home?'Search or enter an address':secure?'HTTPS connection':'Connection is not encrypted';
    $('start').hidden=!state.home || !!state.error; $('error').hidden=!state.error;
    $('error-message').textContent=state.error || '';
    updateCaptureSettings();
    $('content').setAttribute('aria-label',state.home?'New tab':state.tabs.find(t=>t.id===state.selected)?.title || 'Web page');
    for (const id of observedVisits.keys()) if (!state.tabs.some(tab=>tab.id===id)) observedVisits.delete(id);
    if (state.address && safeURL(state.address) && !state.loading && !state.home && !state.error) {
      const tab=state.tabs.find(t=>t.id===state.selected);
      const visited=observedVisits.get(state.selected)!==state.address || (previous.selected===state.selected && previous.loading);
      observedVisits.set(state.selected,state.address);
      if (visited && history[0]?.url!==state.address) { history=[{url:state.address,title:tab?.title || state.address},...history.filter(h=>h.url!==state.address)].slice(0,50); write('veyra.history',history); }
    }
    if (state.message && state.message!==lastMessage) toast(state.message);
    lastMessage=state.message;
    if ((previous.selected!==state.selected || !previous.home) && state.home && !panelOpen) requestAnimationFrame(()=>{$('search').value=''; $('search').focus();});
  }
  let tabsSignature = '';
  function renderTabs() {
    const signature=JSON.stringify([state.tabs,state.selected]);
    if (signature===tabsSignature) return;
    const focusedTab=document.activeElement?.closest('[data-tab]')?.dataset.tab;
    tabsSignature=signature; $('tabs').replaceChildren();
    state.tabs.forEach((tab,index) => {
      const el=document.createElement('div'); el.className=`tab${tab.pinned?' pinned':''}${tab.loading?' loading':''}`;
      el.dataset.tab=tab.id; el.dataset.active=String(tab.id===state.selected); el.setAttribute('role','presentation');el.draggable=true;
      const select=document.createElement('button'); select.className='tab-select';select.tabIndex=tab.id===state.selected?0:-1;select.setAttribute('role','tab');select.setAttribute('aria-selected',String(tab.id===state.selected));select.setAttribute('aria-label',`${tab.home?'New tab':tab.title}${tab.pinned?', pinned':''}`);select.id=`tab-${tab.id}`;select.setAttribute('aria-controls','content');
      select.innerHTML=`<span class="tab-glyph">${svg(tab.loading?'reload':tab.pinned?'pin':'globe')}</span><span class="tab-title"></span>`;
      select.querySelector('.tab-title').textContent=tab.home?'New tab':tab.title;
      select.addEventListener('click',()=>send('selectTab',tab.id));
      const close=document.createElement('button'); close.className='icon-button tab-close'; close.innerHTML=svg('close');
      close.setAttribute('aria-label',`Close ${tab.title}`); close.addEventListener('click',()=>send('closeTab',tab.id));
      el.append(select,close);
      el.addEventListener('click',e=>{if(e.target===el)send('selectTab',tab.id);});
      el.addEventListener('keydown',e=>{
        if(e.target!==select) return;
        let target;
        if(e.key==='ArrowRight')target=(index+1)%state.tabs.length;
        if(e.key==='ArrowLeft')target=(index-1+state.tabs.length)%state.tabs.length;
        if(e.key==='Home')target=0; if(e.key==='End')target=state.tabs.length-1;
        if(target!==undefined){e.preventDefault();send('selectTab',state.tabs[target].id);}
        if(e.key==='Enter'||e.key===' '){e.preventDefault();send('selectTab',tab.id);}
        if(e.key==='Delete')send('closeTab',tab.id);
      });
      el.addEventListener('contextmenu',e=>{e.preventDefault();send('pinTab',tab.id);});
      el.addEventListener('dragstart',e=>e.dataTransfer.setData('text/plain',tab.id));
      el.addEventListener('dragover',e=>e.preventDefault());
      el.addEventListener('drop',e=>{e.preventDefault();send('moveTab',JSON.stringify({id:e.dataTransfer.getData('text/plain'),before:tab.id}));});
      $('tabs').append(el);
    });
    const selected=[...$('tabs').children].find(el=>el.dataset.tab===state.selected);
    selected?.scrollIntoView({block:'nearest',inline:'nearest'});
    if(focusedTab) selected?.querySelector('.tab-select').focus();
    if(selected) $('content').setAttribute('aria-labelledby',`tab-${state.selected}`);
  }
  function renderShortcuts() {
    $('shortcuts').replaceChildren();
    shortcuts.forEach(item=>{const button=document.createElement('button');button.className='shortcut';
      const symbol=document.createElement('span');symbol.className='shortcut-symbol';symbol.textContent=item.title.slice(0,1).toUpperCase();
      const label=document.createElement('span');label.className='shortcut-title';label.textContent=item.title;
      button.append(symbol,label);button.title=item.url;button.setAttribute('aria-label',`Open ${item.title}`);
      button.onclick=()=>navigate(item.url);$('shortcuts').append(button);});
  }
  function navigate(input) {
    const value=input.trim();closeSuggestions();closePanel();
    if(!value){send('home');return;}
    let address=safeURL(value);
    if(!address && !/\s/.test(value) && (value.includes('.') || /^localhost(?::\d+)?(?:[/?#]|$)/i.test(value))) {
      address=safeURL((/^localhost(?::\d+)?(?:[/?#]|$)/i.test(value)?'http://':'https://')+value);
    }
    $('address').blur();send('navigate',address || `https://www.google.com/search?q=${encodeURIComponent(value)}`);
  }
  $('address-form').onsubmit=e=>{e.preventDefault();navigate(activeSuggestion>=0?matches[activeSuggestion].url:$('address').value);};
  $('search-form').onsubmit=e=>{e.preventDefault();const query=$('search').value.trim();if(query)navigate(`https://www.google.com/search?q=${encodeURIComponent(query)}`);};
  document.querySelectorAll('[data-action]').forEach(button=>button.onclick=()=>send(button.dataset.action==='reload' && state.loading?'stop':button.dataset.action));
  function closeSuggestions() { if(!suggestionOpen)return; suggestionOpen=false; activeSuggestion=-1; $('suggestions').hidden=true;$('address').setAttribute('aria-expanded','false');$('address').removeAttribute('aria-activedescendant');overlay(); }
  function suggest() {
    const query=$('address').value.trim(); if(!query){closeSuggestions();return;}
    matches=[{title:query,url:`https://www.google.com/search?q=${encodeURIComponent(query)}`,group:'Google search'},
      ...state.favorites.filter(f=>`${f.title} ${f.url}`.toLowerCase().includes(query.toLowerCase())).slice(0,3).map(f=>({...f,group:'Bookmarks'})),
      ...history.filter(h=>`${h.title} ${h.url}`.toLowerCase().includes(query.toLowerCase())).slice(0,3).map(h=>({...h,group:'History'}))];
    activeSuggestion=-1; $('suggestions').replaceChildren(); let group='';
    matches.forEach((item,index)=>{if(item.group!==group){const heading=document.createElement('div');heading.className='suggestion-group';heading.setAttribute('role','presentation');heading.textContent=item.group;$('suggestions').append(heading);group=item.group;}
      const el=document.createElement('div');el.className='suggestion';el.id=`suggestion-${index}`;el.setAttribute('role','option');el.setAttribute('aria-selected','false');
      const icon=document.createElement('span');icon.innerHTML=svg(item.group==='Google search'?'search':item.group==='Bookmarks'?'star':'globe');
      const label=document.createElement('div'),primary=document.createElement('strong'),secondary=document.createElement('small');
      primary.textContent=item.title;secondary.textContent=item.group==='Google search'?'Search with Google':item.url;label.append(primary,secondary);el.append(icon,label);
      el.onmousedown=e=>{e.preventDefault();navigate(item.url);};$('suggestions').append(el);});
    const rect=$('address-form').getBoundingClientRect();$('suggestions').style.left=`${rect.left}px`;$('suggestions').style.right=`${innerWidth-rect.right}px`;
    suggestionOpen=true;$('suggestions').hidden=false;$('address').setAttribute('aria-expanded','true');overlay();
  }
  $('address').addEventListener('input',suggest);
  $('address').addEventListener('focus',()=>$('address').select());
  $('address').addEventListener('keydown',e=>{if(!suggestionOpen || !['ArrowDown','ArrowUp'].includes(e.key))return;e.preventDefault();activeSuggestion=(activeSuggestion+(e.key==='ArrowDown'?1:-1)+matches.length)%matches.length;
    [...$('suggestions').querySelectorAll('[role="option"]')].forEach((el,i)=>el.setAttribute('aria-selected',String(i===activeSuggestion)));
    $('address').setAttribute('aria-activedescendant',`suggestion-${activeSuggestion}`);});
  document.addEventListener('pointerdown',e=>{if(!e.target.closest('.omnibox,.suggestions'))closeSuggestions();});
  function openPanel(title) { closeSuggestions();focusReturn=document.activeElement; panelOpen=true; $('panel-title').textContent=title;$('panel-body').replaceChildren();$('panel-backdrop').hidden=false;document.querySelector('.chrome').inert=true;$('content').inert=true;overlay();$('panel').focus(); }
  function closePanel() { if(!panelOpen)return;panelOpen=false;$('panel-backdrop').hidden=true;document.querySelector('.chrome').inert=false;$('content').inert=false;overlay();focusReturn?.focus(); }
  function heading(text) { const el=document.createElement('h2');el.textContent=text;$('panel-body').append(el); }
  function action(label,fn,icon='forward') {const button=document.createElement('button');button.className='panel-action';button.innerHTML=svg(icon);const text=document.createElement('span');text.textContent=label;button.append(text);button.onclick=fn;$('panel-body').append(button);}
  function updateCaptureSettings() {
    const button=$('capture-toggle'),status=$('capture-status');
    if(!button || !status)return;
    button.querySelector('span').textContent=state.capture==='active'?'Disable screen capture protection':'Enable screen capture protection';
    button.disabled=state.capture==='unavailable';
    status.textContent=state.capture==='active'?'Screen capture protection is active.':state.capture==='disabled'?'Screen capture protection is disabled.':'Screen capture protection is unavailable on this platform.';
  }
  $('settings-button').onclick=()=>{
    openPanel('Settings');heading('Appearance');const segment=document.createElement('div');segment.className='segment';segment.setAttribute('role','group');segment.setAttribute('aria-label','Color theme');
    ['light','dark','system'].forEach(value=>{const button=document.createElement('button');button.textContent=value[0].toUpperCase()+value.slice(1);button.setAttribute('aria-pressed',String(value===theme));button.onclick=()=>{theme=value;document.documentElement.dataset.theme=theme;write('veyra.theme',theme);[...segment.children].forEach(el=>el.setAttribute('aria-pressed',String(el===button)));};segment.append(button);});$('panel-body').append(segment);
    heading('Page zoom');action('Zoom in',()=>send('zoomIn'),'plus');action('Zoom out',()=>send('zoomOut'),'minus');action('Reset zoom',()=>send('zoomReset'));
    heading('Privacy');action('',()=>send('toggleCapture'),'shield');$('panel-body').lastElementChild.id='capture-toggle';const protection=document.createElement('p');protection.id='capture-status';protection.setAttribute('role','status');$('panel-body').append(protection);updateCaptureSettings();
    action('Clear cookies and cache',()=>send('clearData'));action('Clear local suggestion history',()=>{history=[];write('veyra.history',history);toast('Suggestion history cleared.');});
  };
  $('overflow-button').onclick=()=>{
    openPanel('Bookmarks & tabs');heading('Bookmarks');
    if(!state.favorites.length){const p=document.createElement('p');p.textContent='Save a page with the star in the address bar.';$('panel-body').append(p);}
    state.favorites.forEach(f=>{action(f.title,()=>navigate(f.url),'star');action(`Remove ${f.title}`,()=>{send('removeFavorite',f.url);closePanel();},'close');});
    heading('Tabs');action(state.tabs.find(t=>t.id===state.selected)?.pinned?'Unpin current tab':'Pin current tab',()=>{send('pinTab',state.selected);closePanel();},'pin');
    action('Duplicate current tab',()=>{send('duplicate');closePanel();});action('Reopen closed tab',()=>{send('restore');closePanel();});action('Close other tabs',()=>{send('closeOthers');closePanel();});action('Clear saved session',()=>send('clearSession'));
    heading('Extensions');const p=document.createElement('p');p.textContent='Extensions are unavailable in this browser.';$('panel-body').append(p);
  };
  $('customize').onclick=()=>{
    openPanel('Customize shortcuts');const form=document.createElement('form');
    shortcuts.forEach((item,i)=>{const row=document.createElement('div');row.className='edit-row';
      const label=document.createElement('label');label.textContent=`Shortcut ${i+1}`;
      const title=document.createElement('input');title.value=item.title;title.required=true;title.maxLength=32;title.setAttribute('aria-label',`Shortcut ${i+1} name`);
      const url=document.createElement('input');url.value=item.url;url.type='url';url.required=true;url.setAttribute('aria-label',`Shortcut ${i+1} URL`);
      label.append(title);row.append(label,url);form.append(row);});
    const save=document.createElement('button');save.className='text-button';save.textContent='Save shortcuts';form.append(save);
    form.onsubmit=e=>{e.preventDefault();const edited=[...form.querySelectorAll('.edit-row')].map(row=>{const [title,url]=row.querySelectorAll('input');return{title:title.value.trim(),url:safeURL(url.value)};});if(edited.some(s=>!s.title||!s.url))return toast('Use a name and an HTTP or HTTPS address for every shortcut.');shortcuts=edited;write('veyra.shortcuts',shortcuts);renderShortcuts();closePanel();};$('panel-body').append(form);
  };
  $('panel-close').onclick=closePanel;$('panel-backdrop').onpointerdown=e=>{if(e.target===$('panel-backdrop'))closePanel();};
  document.addEventListener('keydown',e=>{
    if(e.key==='Escape'){closeSuggestions();closePanel();}
    if(panelOpen && e.key==='Tab'){const items=[...$('panel').querySelectorAll('button,input,select')].filter(el=>!el.disabled);const first=items[0],last=items.at(-1);if(e.shiftKey&&(document.activeElement===first||document.activeElement===$('panel'))){e.preventDefault();last.focus();}else if(!e.shiftKey&&(document.activeElement===last||document.activeElement===$('panel'))){e.preventDefault();first.focus();}}
    if(e.ctrlKey||e.metaKey){const key=e.key.toLowerCase();if(key==='l'){e.preventDefault();closePanel();$('address').focus();$('address').select();}if(key==='t'){e.preventDefault();send(e.shiftKey?'restore':'newTab');}if(key==='w'){e.preventDefault();send('closeTab',state.selected);}if(key==='r'){e.preventDefault();send('reload');}if(key==='d'){e.preventDefault();send('bookmark');}}
  });
  $('titlebar').addEventListener('mousedown',e=>{if(e.button===0&&!e.target.closest('button,input,.tabs,.window-controls'))send(e.detail===2?'maximize':'drag');});
  $('tabs').addEventListener('wheel',e=>{if(Math.abs(e.deltaY)>Math.abs(e.deltaX)){$('tabs').scrollLeft+=e.deltaY;e.preventDefault();}},{passive:false});
  window.veyra={receive:message=>{if(message.type==='pagePreview'&&(panelOpen||suggestionOpen)&&!state.home&&!state.error&&state.ready&&message.image?.startsWith('data:image/png;base64,')){$('page-preview').src=message.image;$('page-preview').hidden=false;}if(message.type==='preferences') {
    const prefs=message.preferences || {};
    if(['light','dark','system'].includes(prefs['veyra.theme'])) { theme=prefs['veyra.theme'];document.documentElement.dataset.theme=theme; }
    const saved=prefs['veyra.shortcuts'];
    if(Array.isArray(saved)&&saved.length===8&&saved.every(s=>typeof s?.title==='string'&&safeURL(s.url))) { shortcuts=saved;renderShortcuts(); }
    const visits=prefs['veyra.history'];
    if(Array.isArray(visits))history=visits.filter(h=>typeof h?.title==='string'&&safeURL(h.url)).slice(0,50);
  }if(message.type==='state')render(message.state);if(message.type==='focusAddress'){closePanel();$('address').focus();$('address').select();}}};
  window.chrome?.webview?.addEventListener('message',e=>window.veyra.receive(e.data));
  renderShortcuts();render(state);send('ready');
  if(!native) $('search').focus();
})();
