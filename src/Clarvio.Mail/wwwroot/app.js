const root = document.querySelector('#app');
const state = { session: null, providers: [], mailboxes: [], mailbox: null, view: 'inbox', folder: 'inbox', query: '', messages: [], cursor: null, selected: null, loading: false, folders: [], error: '', notice: '' };
let requestVersion = 0;
const escape = value => String(value ?? '').replace(/[&<>"']/g, character => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[character]));
const date = value => value ? new Date(value).toLocaleString(undefined, {dateStyle:'medium',timeStyle:'short'}) : '—';
const brand = '<div class="brand"><img src="/mark.svg" alt="">Clarvio Mail</div>';
const mailboxPath = () => `/mailboxes/${encodeURIComponent(state.mailbox)}`;
class ApiFailure extends Error { constructor(message, status, code) { super(message); this.status = status; this.code = code; } }
async function api(path, { method = 'GET', body, headers = {} } = {}) {
  const response = await fetch('/api/v1' + path, {
    method, credentials: 'same-origin', headers: { ...(body ? {'Content-Type':'application/json'} : {}),
      ...(method !== 'GET' && state.session ? {'X-CSRF-Token':state.session.csrfToken} : {}), ...headers },
    body: body ? JSON.stringify(body) : undefined
  });
  const data = response.status === 204 ? null : await response.json().catch(() => null);
  if (!response.ok) throw new ApiFailure(data?.error?.message ?? 'The service could not complete this request.', response.status, data?.error?.code);
  return data;
}
function providerButtons(connect = false) {
  return state.providers.map(provider => `<a class="provider-button ${provider.configured ? '' : 'disabled'}" ${provider.configured ? `href="/auth/${provider.provider}${connect ? '?mode=connect' : ''}"` : 'aria-disabled="true"'}>
    ${provider.provider === 'outlook' ? 'Microsoft' : 'Google'} ${connect ? '— connect mailbox' : '— sign in'} ${provider.configured ? '' : '<span class="badge">Not configured</span>'}</a>`).join('');
}
function login() {
  root.innerHTML = `<main class="login"><section class="login-story">${brand}<div><span class="eyebrow">Your email, connected</span><h1>A clear view of every conversation.</h1><p>Connect Microsoft and Gmail accounts. Read, reply, and send from one workspace.</p></div><p class="fine">Clarvio Mail · Local development</p></section>
    <section class="login-card"><div><span class="eyebrow">Welcome to your workspace</span><h2>Connect your email.</h2><p>Sign in with the mailbox you want to connect. You control which accounts have access.</p>${state.error ? `<div class="error" role="alert">${escape(state.error)}</div>` : ''}${providerButtons()}<p class="fine">Your account password stays with Microsoft or Google. Clarvio Mail uses the permissions you authorize.</p></div></section></main>`;
}
function shell() {
  const title = {inbox:'Email workspace',accounts:'Connected accounts',keys:'API keys',webhooks:'Webhooks',audit:'Activity'}[state.view];
  root.innerHTML = `<div class="shell"><aside class="sidebar">${brand}<nav aria-label="Workspace">
    ${Object.entries({inbox:'Inbox',accounts:'Accounts',keys:'API keys',webhooks:'Webhooks',audit:'Activity'}).map(([view,label]) => `<button data-view="${view}" class="${state.view===view?'active':''}">${label}</button>`).join('')}
    </nav><footer>${escape(state.session.user.email)}<br><button class="quiet" id="logout">Sign out</button></footer></aside><main class="workspace"><header class="topbar"><h1>${title}</h1>
    ${state.view==='inbox' ? `<select id="mailbox" aria-label="Mailbox"><option value="">Select a mailbox</option>${state.mailboxes.map(box => `<option value="${box.id}" ${state.mailbox===box.id?'selected':''}>${escape(box.email)}${box.status!=='active'?' · '+escape(box.status):''}</option>`).join('')}</select><button class="primary" id="compose" ${activeMailbox()?'':'disabled'}>+ Compose</button>` : ''}</header>
    <section class="content"><div id="feedback">${feedback()}</div><div id="view"></div></section></main></div>`;
  root.querySelectorAll('[data-view]').forEach(button => button.addEventListener('click', () => {requestVersion++;state.view=button.dataset.view;state.error='';state.notice='';shell();void renderView();}));
  root.querySelector('#logout')?.addEventListener('click', async () => {try {await api('/logout',{method:'POST'});state.session=null;login();}catch(error){showError(error);}});
  root.querySelector('#mailbox')?.addEventListener('change', event => {state.mailbox=event.target.value;state.folders=[];state.folder='inbox';state.messages=[];state.selected=null;state.cursor=null;state.error='';shell();void loadMessages();});
  root.querySelector('#compose')?.addEventListener('click', () => compose());
}
function activeMailbox() { return state.mailboxes.find(box=>box.id===state.mailbox)?.status==='active'; }
function feedback() {return (state.error?`<div class="error" role="alert">${escape(state.error)}</div>`:'')+(state.notice?`<div class="notice" role="status">${escape(state.notice)}</div>`:'');}
function updateFeedback() {const target=root.querySelector('#feedback');if(target)target.innerHTML=feedback();}
function showError(error) {state.error=error.message||'The request failed.';updateFeedback();}
async function renderView() {
  const version = requestVersion;
  try {
    if(state.view==='inbox') {renderInbox();if(!state.messages.length)await loadMessages();return;}
    root.querySelector('#view').innerHTML='<div class="empty">Loading…</div>';
    if(state.view==='accounts') {await refreshMailboxes();if(version===requestVersion)renderAccounts();return;}
    const data=await api('/'+state.view);if(version!==requestVersion)return;
    if(state.view==='keys')renderKeys(data);
    else if(state.view==='webhooks')renderWebhooks(data);
    else if(state.view==='audit')renderAudit(data);
  } catch(error) {if(version===requestVersion)showError(error);}
}
async function refreshMailboxes() {state.mailboxes=await api('/mailboxes');if(!state.mailbox)state.mailbox=state.mailboxes.find(box=>box.status==='active')?.id??null;}
function renderInbox() {
  const view=root.querySelector('#view');if(!view)return;
  if(!state.mailbox) {view.innerHTML='<div class="panel empty"><h2>Connect a mailbox to get started.</h2><p>Open Accounts to add your Microsoft or Google account.</p></div>';return;}
  if(!activeMailbox()) {view.innerHTML='<div class="panel empty"><h2>This mailbox needs to be connected again.</h2><p>Open Accounts and reconnect with its original account.</p></div>';return;}
  view.innerHTML=`<div class="toolbar"><select id="folder" aria-label="Folder">${(state.folders.length?state.folders:[{id:'inbox',name:'Inbox'}]).map(folder=>`<option value="${escape(folder.id)}" ${state.folder===folder.id?'selected':''}>${escape(folder.name)}</option>`).join('')}</select><form id="search"><input name="query" aria-label="Search email" placeholder="Search email…" maxlength="300" value="${escape(state.query)}"><button>Search</button></form><button id="refresh">Refresh</button></div>
    <div class="inbox"><section class="message-list" aria-label="Messages">${state.loading&&!state.messages.length?'<div class="empty">Loading messages…</div>':state.messages.length?state.messages.map(message=>`<button class="message-row ${state.selected?.id===message.id?'selected':''}" data-message="${escape(message.id)}"><span class="meta"><span>${escape(message.from?.name||message.from?.email||'Unknown sender')}</span><span>${escape(message.receivedAt?new Date(message.receivedAt).toLocaleDateString(): '')}</span></span><span class="subject">${message.isRead===false?'<strong>':''}${escape(message.subject||'(No subject)')}${message.isRead===false?'</strong>':''}</span><span class="preview">${escape(message.preview)}</span></button>`).join(''):'<div class="empty">No messages found.</div>'}${state.cursor?'<button class="load-more" id="more">Load more</button>':''}</section><article class="reader" id="reader">${state.selected?readerMarkup(state.selected):'<div class="empty">Select a conversation to read it.</div>'}</article></div>`;
  view.querySelector('#folder').addEventListener('change',event=>{state.folder=event.target.value;void loadMessages();});
  view.querySelector('#search').addEventListener('submit',event=>{event.preventDefault();state.query=new FormData(event.target).get('query').trim();void loadMessages();});
  view.querySelector('#refresh').addEventListener('click',()=>void loadMessages());
  view.querySelector('#more')?.addEventListener('click',()=>void loadMessages(true));
  view.querySelectorAll('[data-message]').forEach(button=>button.addEventListener('click',()=>void selectMessage(button.dataset.message)));
  bindReader();
}
async function loadMessages(more=false) {
  const version=++requestVersion;if(!activeMailbox()){renderInbox();return;}
  state.loading=true;state.error='';if(!more){state.messages=[];state.selected=null;state.cursor=null;}renderInbox();updateFeedback();
  try {
    if(!state.folders.length){const folders=await api(mailboxPath()+'/folders');if(version!==requestVersion)return;state.folders=folders;}
    const params=new URLSearchParams({folder:state.folder,limit:'25'});if(state.query)params.set('query',state.query);if(more&&state.cursor)params.set('cursor',state.cursor);
    const data=await api(mailboxPath()+'/messages?'+params);if(version!==requestVersion)return;
    state.messages=more?[...state.messages,...data.messages.filter(message=>!state.messages.some(existing=>existing.id===message.id))]:data.messages;state.cursor=data.nextCursor;
  }catch(error){if(version===requestVersion)showError(error);}
  finally{if(version===requestVersion){state.loading=false;renderInbox();}}
}
async function selectMessage(id) {
  const version=++requestVersion;const selectedBox=state.mailbox;
  root.querySelector('#reader').innerHTML='<div class="empty">Loading conversation…</div>';
  try{const row=state.messages.find(message=>message.id===id);const message=await api(mailboxPath()+(row?.draftId?'/drafts/'+encodeURIComponent(row.draftId):'/messages/'+encodeURIComponent(id)));if(version!==requestVersion||selectedBox!==state.mailbox)return;state.selected=message;renderInbox();}
  catch(error){if(version===requestVersion){showError(error);renderInbox();}}
}
function addresses(values) {return (values??[]).map(address=>address.name?`${address.name} <${address.email}>`:address.email).join(', ');}
function safeText(message) {
  if(message.textBody!==null&&message.textBody!==undefined)return message.textBody;
  if(message.htmlBody){const template=document.createElement('template');template.innerHTML=message.htmlBody;template.content.querySelectorAll('script,style,iframe,object,template').forEach(node=>node.remove());return template.content.textContent??'';}
  return message.preview;
}
function readerMarkup(message) {
  return `<span class="eyebrow">Conversation</span><h2>${escape(message.subject||'(No subject)')}</h2><div class="addresses">From: ${escape(message.from?addresses([message.from]):'Unknown sender')}<br>To: ${escape(addresses(message.to))}${message.cc?.length?'<br>CC: '+escape(addresses(message.cc)):''}${message.draftId&&message.bcc?.length?'<br>BCC: '+escape(addresses(message.bcc)):''}<br>${escape(date(message.receivedAt))}</div><div class="message-body">${escape(safeText(message))}</div>${message.htmlBody&&!message.textBody?'<p class="muted">HTML email displayed as text. External images are not loaded.</p>':''}
    <div class="attachments">${message.attachments.map(file=>`<a class="attachment" href="/api/v1${mailboxPath()}/messages/${encodeURIComponent(message.id)}/attachments/${encodeURIComponent(file.id)}">↓ ${escape(file.name)} · ${Math.ceil(file.size/1024)} KB</a>`).join('')}</div><div class="reader-actions">${message.draftId?'<button id="edit-draft" class="primary">Edit draft</button><button id="delete-draft" class="danger">Delete draft</button>':'<button id="reply">Reply</button><button id="reply-all">Reply all</button><button id="forward">Forward</button><button id="mark-read">'+(message.isRead===false?'Mark read':'Mark unread')+'</button>'+(state.folder!=='trash'?'<button id="trash-message" class="danger">Move to Trash</button>':'')} ${message.draftId?'':'<select id="move-destination" aria-label="Move destination">'+state.folders.filter(folder=>folder.canMove&&folder.id!==state.folder).map(folder=>'<option value="'+escape(folder.id)+'">'+escape(folder.name)+'</option>').join('')+'</select><button id="move-message">Move</button>'}</div>`;
}
function bindReader(){
  root.querySelector('#reply')?.addEventListener('click',()=>compose('reply'));
  root.querySelector('#reply-all')?.addEventListener('click',()=>compose('reply-all'));
  root.querySelector('#forward')?.addEventListener('click',()=>compose('forward'));
  root.querySelector('#edit-draft')?.addEventListener('click',()=>compose('draft'));
  root.querySelector('#delete-draft')?.addEventListener('click',async event=>{
    if(!confirm('Delete this draft? Gmail deletes drafts permanently.'))return;
    event.target.disabled=true;try{await api(mailboxPath()+'/drafts/'+encodeURIComponent(state.selected.draftId),{method:'DELETE'});await loadMessages();}catch(error){showError(error);event.target.disabled=false;}
  });
  root.querySelector('#mark-read')?.addEventListener('click',async event=>{
    event.target.disabled=true;const message=state.selected;const mailbox=state.mailbox;
    try{await api(mailboxPath()+'/messages/'+encodeURIComponent(message.id)+'/read',{method:'PATCH',body:{isRead:message.isRead===false}});if(state.mailbox!==mailbox||state.selected?.id!==message.id)return;message.isRead=message.isRead===false;const row=state.messages.find(row=>row.id===message.id);if(row)row.isRead=message.isRead;renderInbox();}catch(error){showError(error);event.target.disabled=false;}
  });
  const move=async(destination,button)=>{if(!destination)return;button.disabled=true;try{await api(mailboxPath()+'/messages/'+encodeURIComponent(state.selected.id)+'/move',{method:'POST',body:{destinationFolderId:destination,sourceFolderId:state.folder}});await loadMessages();}catch(error){showError(error);button.disabled=false;}};
  root.querySelector('#move-message')?.addEventListener('click',event=>void move(root.querySelector('#move-destination').value,event.target));
  root.querySelector('#trash-message')?.addEventListener('click',event=>void move('trash',event.target));
}
function recipientInput(values){return(values??[]).map(address=>address.email).join(', ');}
function parseRecipients(value){return value.split(/[;,]/).map(email=>email.trim()).filter(Boolean).map(email=>({email}));}
function compose(mode='new') {
  if(document.querySelector('dialog'))return;
  const original=state.selected;const box=state.mailboxes.find(item=>item.id===state.mailbox);const sourceMailbox=state.mailbox;
  const reply=mode==='reply'||mode==='reply-all';let draft=mode==='draft'?original:null;
  let to=draft?draft.to:reply?original.replyTo?.length?original.replyTo:original.from?[original.from]:[]:[];
  let cc=draft?draft.cc:[];
  if(mode==='reply-all'){
    const seen=new Set([...to.map(address=>address.email.toLowerCase()),box.email.toLowerCase()]);
    cc=[...original.to,...original.cc].filter(address=>{const key=address.email.toLowerCase();if(seen.has(key))return false;seen.add(key);return true;});
  }
  const subject=draft?draft.subject:reply?original.subject:mode==='forward'?'Fwd: '+original.subject:'';
  let unchangedText=draft?safeText(draft):mode==='forward'?`\n\n---------- Forwarded message ----------\nFrom: ${addresses(original.from?[original.from]:[])}\nDate: ${date(original.receivedAt)}\nSubject: ${original.subject}\n\n${safeText(original)}`:'';
  const dialog=document.createElement('dialog');
  dialog.innerHTML=`<header><h2>${draft?'Edit draft':reply?'Reply':mode==='forward'?'Forward':'New message'}</h2><button type="button" id="close" class="quiet" aria-label="Close compose">×</button></header><form id="send-form"><p>From ${escape(box.email)}</p><div class="field"><label for="to">To</label><input id="to" name="to" value="${escape(recipientInput(to))}" placeholder="name@example.com, another@example.com"></div><div class="field"><label for="cc">CC</label><input id="cc" name="cc" value="${escape(recipientInput(cc))}"></div><div class="field"><label for="bcc">BCC</label><input id="bcc" name="bcc" value="${escape(recipientInput(draft?.bcc))}"></div><div class="field"><label for="subject">Subject</label><input id="subject" name="subject" value="${escape(subject)}" maxlength="500" ${reply?'readonly':''}></div><div class="field"><label for="body">Message</label><textarea id="body" name="body" maxlength="100000">${escape(unchangedText)}</textarea></div><div class="field"><label for="files">Attachments · 2 MiB combined</label><div id="retained-files"></div><input id="files" name="files" type="file" multiple><div class="file-list" id="file-list"></div></div>${mode==='forward'?'<p>Forwarding includes the message text. Add any files you want to forward as attachments.</p>':''}${draft?.htmlBody?'<p>Editing the message body saves your changes as plain text.</p>':''}<div id="send-status" role="status"></div><footer><button id="cancel" type="button">Cancel</button><button type="submit" value="save" id="save-draft" formnovalidate>Save draft</button><button class="primary" value="send" id="send-button">Send message</button></footer></form>`;
  document.body.append(dialog);dialog.showModal();
  let key=crypto.randomUUID(),fingerprint=null,busy=false,uncertain=false;
  const status=dialog.querySelector('#send-status');
  const renderRetained=()=>{dialog.querySelector('#retained-files').innerHTML=(draft?.attachments??[]).map(file=>`<label class="checkbox"><input type="checkbox" name="keep" value="${escape(file.id)}" checked>${escape(file.name)} (${Math.ceil(file.size/1024)} KB)</label>`).join('');};
  renderRetained();
  const close=()=>{if(!busy){dialog.close();dialog.remove();}};
  dialog.querySelector('#close').addEventListener('click',close);dialog.querySelector('#cancel').addEventListener('click',close);
  dialog.addEventListener('cancel',event=>{event.preventDefault();close();});
  dialog.querySelector('#files').addEventListener('change',event=>{dialog.querySelector('#file-list').textContent=Array.from(event.target.files).map(file=>`${file.name} (${Math.ceil(file.size/1024)} KB)`).join(' · ');});
  dialog.querySelector('form').addEventListener('submit',async event=>{
    event.preventDefault();if(busy||uncertain)return;
    const saving=event.submitter?.value==='save';busy=true;
    dialog.querySelectorAll('button').forEach(button=>button.disabled=true);
    status.innerHTML='<p>'+ (saving?'Saving draft…':'Sending…')+'</p>';
    let phase='validate';
    try{
      const form=new FormData(event.target);dialog.querySelectorAll('input,textarea').forEach(input=>input.disabled=true);const files=Array.from(dialog.querySelector('#files').files);const keep=form.getAll('keep');
      const retained=(draft?.attachments??[]).filter(file=>keep.includes(file.id));
      if(files.length+retained.length>10||files.reduce((sum,file)=>sum+file.size,0)+retained.reduce((sum,file)=>sum+file.size,0)>2*1024*1024)throw new Error('Use at most 10 attachments, up to 2 MiB combined.');
      const to=parseRecipients(form.get('to'));if(!saving&&(!to.length||!form.get('subject').trim()))throw new Error('Add a To recipient and subject before sending.');
      const attachments=await Promise.all(files.map(async file=>{const data=await new Promise((resolve,reject)=>{const reader=new FileReader();reader.onload=()=>resolve(reader.result);reader.onerror=()=>reject(new Error('Could not read the attachment.'));reader.readAsDataURL(file);});return{name:file.name,contentType:file.type||'application/octet-stream',contentBase64:data.slice(data.indexOf(',')+1)};}));
      const request={to,cc:parseRecipients(form.get('cc')),bcc:parseRecipients(form.get('bcc')),subject:form.get('subject'),textBody:form.get('body'),attachments};
      if(draft?.htmlBody&&request.textBody===unchangedText)request.htmlBody=draft.htmlBody;
      const serialized=JSON.stringify({request,keep,draftId:draft?.draftId});
      if(fingerprint!==null&&fingerprint!==serialized)key=crypto.randomUUID();fingerprint=serialized;
      if(saving||draft){
        phase='save';
        const saved=await api(`/mailboxes/${sourceMailbox}/drafts${draft?'/'+encodeURIComponent(draft.draftId):''}`,{method:draft?'PUT':'POST',body:{message:request,keepAttachmentIds:keep,replyMessageId:reply&&!draft?original.id:undefined}});
        draft=saved;unchangedText=request.textBody;renderRetained();dialog.querySelector('#files').value='';dialog.querySelector('#file-list').textContent='';
        if(saving){
          status.innerHTML='<div class="notice">Draft saved. You can reopen it from Drafts.</div>';
          if(state.mailbox===sourceMailbox&&state.folder==='drafts')void loadMessages();return;
        }
      }
      phase='send';
      const result=draft?await api(`/mailboxes/${sourceMailbox}/drafts/${encodeURIComponent(draft.draftId)}/send`,{method:'POST',headers:{'Idempotency-Key':key}}):await api(`/mailboxes/${sourceMailbox}/messages${reply?'/'+encodeURIComponent(original.id)+'/reply':''}`,{method:'POST',body:request,headers:{'Idempotency-Key':key}});
      if(result.status==='accepted'){state.notice='The email provider accepted your message. Delivery may take a moment.';state.error='';updateFeedback();busy=false;close();if(state.mailbox===sourceMailbox&&state.folder==='drafts')void loadMessages();return;}
      if(result.status==='unknown'||result.status==='sending'){
        uncertain=true;status.innerHTML=`<div class="notice">The send outcome is ${escape(result.status)}. Check Sent mail before sending again.<br>Operation: ${escape(result.id)}<br>Idempotency key: ${escape(key)}</div>`;
      }else{
        status.innerHTML=`<div class="error">The provider did not accept the message (${escape(result.errorCode)}). You can try again.</div>`;key=crypto.randomUUID();fingerprint=null;
      }
    }catch(error){
      if(phase!=='validate'&&(!(error instanceof ApiFailure)||error.status>=500)){
        uncertain=true;status.innerHTML=phase==='save'?'<div class="notice">The draft save could not be confirmed. Reopen Drafts to check its latest content before trying again.</div>':`<div class="notice">The connection ended before confirmation. Check Sent mail before sending again.<br>Idempotency key: ${escape(key)}</div>`;
      }else status.innerHTML=`<div class="error">${escape(error.message)}</div>`;
    }finally{busy=false;dialog.querySelectorAll('input,textarea').forEach(input=>input.disabled=false);dialog.querySelectorAll('button').forEach(button=>button.disabled=uncertain&&button.id!=='cancel'&&button.id!=='close');}
  });
}
function renderAccounts(){
  root.querySelector('#view').innerHTML=`<div class="panel"><h2>Add an account</h2><p>Connect additional accounts while signed in to this workspace.</p>${providerButtons(true)}</div><div class="panel table-wrap"><h2>Your mailboxes</h2><table><thead><tr><th>Account</th><th>Provider</th><th>Status</th><th></th></tr></thead><tbody>${state.mailboxes.map(box=>`<tr><td>${escape(box.email)}</td><td>${escape(box.provider)}</td><td><span class="status ${escape(box.status)}">${escape(box.status)}</span></td><td>${box.status==='active'?`<button class="danger" data-disconnect="${box.id}">Disconnect</button>`:''}</td></tr>`).join('')}</tbody></table></div>`;
  root.querySelectorAll('[data-disconnect]').forEach(button=>button.addEventListener('click',async()=>{if(!confirm('Disconnect this mailbox and disable its webhooks?'))return;try{await api('/mailboxes/'+button.dataset.disconnect,{method:'DELETE'});state.mailbox=null;await refreshMailboxes();renderAccounts();}catch(error){showError(error);}}));
}
function renderKeys(keys){
  root.querySelector('#view').innerHTML=`<div class="panel"><h2>Create an API key</h2><p>Keys access only your connected accounts. The full key is displayed once.</p><form id="create-key"><input name="name" aria-label="Key name" placeholder="Integration name" required maxlength="80"><input name="days" aria-label="Expiry in days" type="number" min="1" max="365" value="90"><div>${['accounts:read','mail:read','mail:write','mail:send'].map(scope=>`<label class="checkbox"><input type="checkbox" name="scope" value="${scope}" ${scope==='accounts:read'||scope==='mail:read'?'checked':''}>${scope}</label>`).join('')}</div><button class="primary">Create key</button></form><div id="new-key"></div></div><div class="panel table-wrap"><table><thead><tr><th>Name / prefix</th><th>Scopes</th><th>Expires</th><th>Status</th><th></th></tr></thead><tbody>${keys.map(key=>`<tr><td>${escape(key.name)}<br><span class="muted">${escape(key.prefix)}…</span></td><td>${escape(key.scopes.join(', '))}</td><td>${escape(date(key.expiresAt))}</td><td>${key.revokedAt?'Revoked':new Date(key.expiresAt)<new Date()?'Expired':'Active'}</td><td>${key.revokedAt?'':`<button data-revoke="${key.id}">Revoke</button>`}</td></tr>`).join('')}</tbody></table></div>`;
  root.querySelector('#create-key').addEventListener('submit',async event=>{event.preventDefault();const form=new FormData(event.target);try{const key=await api('/keys',{method:'POST',body:{name:form.get('name'),scopes:form.getAll('scope'),expiresInDays:Number(form.get('days'))}});root.querySelector('#new-key').innerHTML=`<p>Save this key securely. It will not be displayed again.</p><code class="secret">${escape(key.token)}</code>`;event.target.reset();}catch(error){showError(error);}});
  root.querySelectorAll('[data-revoke]').forEach(button=>button.addEventListener('click',async()=>{try{await api('/keys/'+button.dataset.revoke,{method:'DELETE'});void renderView();}catch(error){showError(error);}}));
}
function renderWebhooks(subscriptions){
  root.querySelector('#view').innerHTML=`<div class="panel"><h2>Incoming email events</h2><p>Receive signed HTTPS events for new inbox messages. Destination hosts must be enabled in the service configuration. Initial synchronization starts from the current mailbox state.</p><form id="create-webhook"><select name="mailbox" aria-label="Webhook mailbox">${state.mailboxes.filter(box=>box.status==='active').map(box=>`<option value="${box.id}">${escape(box.email)}</option>`).join('')}</select><input name="url" type="url" aria-label="Webhook URL" placeholder="https://your-service.example/webhook" required><button class="primary">Create webhook</button></form><div id="new-webhook"></div></div><div class="panel table-wrap"><table><thead><tr><th>Destination</th><th>Status</th><th></th></tr></thead><tbody>${subscriptions.map(sub=>`<tr><td>${escape(sub.url)}</td><td>${sub.active?'Active':'Disabled'}</td><td>${sub.active?`<button data-disable="${sub.id}">Disable</button>`:''}</td></tr>`).join('')}</tbody></table></div><div class="panel"><h2>Recent deliveries</h2><div id="deliveries">Loading…</div></div>`;
  root.querySelector('#create-webhook').addEventListener('submit',async event=>{event.preventDefault();const form=new FormData(event.target);try{const sub=await api('/webhooks',{method:'POST',body:{mailboxId:form.get('mailbox'),url:form.get('url')}});root.querySelector('#new-webhook').innerHTML=`<p>Save your signing secret securely. Verify signatures before processing events.</p><code class="secret">${escape(sub.signingSecret)}</code>`;}catch(error){showError(error);}});
  root.querySelectorAll('[data-disable]').forEach(button=>button.addEventListener('click',async()=>{try{await api('/webhooks/'+button.dataset.disable,{method:'DELETE'});void renderView();}catch(error){showError(error);}}));
  const version=requestVersion;void api('/webhook-deliveries').then(data=>{if(version!==requestVersion)return;root.querySelector('#deliveries').innerHTML=data.length?`<div class="table-wrap"><table><thead><tr><th>Event</th><th>Status</th><th>Attempts</th><th>HTTP</th></tr></thead><tbody>${data.map(item=>`<tr><td>${escape(item.eventId)}</td><td>${escape(item.status)}</td><td>${item.attempts}</td><td>${item.lastStatusCode??'—'}</td></tr>`).join('')}</tbody></table></div>`:'<p>No deliveries yet.</p>';}).catch(showError);
}
function renderAudit(events){root.querySelector('#view').innerHTML=`<div class="panel"><h2>Recent activity</h2><p>Connection changes, key management and send outcomes. Email bodies are not recorded here.</p><div class="table-wrap"><table><thead><tr><th>Action</th><th>Outcome</th><th>Time</th></tr></thead><tbody>${events.map(event=>`<tr><td>${escape(event.action)}</td><td><span class="status ${escape(event.outcome)}">${escape(event.outcome)}</span></td><td>${escape(date(event.createdAt))}</td></tr>`).join('')}</tbody></table></div>${events.length?'':'<p>No activity yet.</p>'}</div>`;}
async function init(){
  try{
    state.providers=await api('/providers');
    try{state.session=await api('/session');}catch(error){if(error.status!==401)throw error;}
    if(!state.session){login();return;}
    await refreshMailboxes();shell();await renderView();
  }catch(error){state.error=error.message;login();}
}
void init();
