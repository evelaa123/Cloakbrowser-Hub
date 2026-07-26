import { render } from 'preact';
import './styles/app.css';
import { App } from './App';
import { ToastProvider } from './components/toast';
import { HubProvider } from './state';

const root = document.getElementById('root');
if (!root) throw new Error('Root element missing from index.html');

// Dark until settings load, so there is no white flash on a dark theme.
document.documentElement.dataset['theme'] = 'dark';

render(
  <ToastProvider>
    <HubProvider>
      <App />
    </HubProvider>
  </ToastProvider>,
  root,
);
