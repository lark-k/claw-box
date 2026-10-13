import React from 'react';
import { createRoot } from 'react-dom/client';
import { add } from './calculator.js';
createRoot(document.getElementById('root')).render(<h1>Sum: {add(2, 3)}</h1>);
